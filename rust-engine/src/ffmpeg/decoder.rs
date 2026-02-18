// FFmpeg Decoder 모듈 (ffmpeg-next with hardware acceleration)
// 아키텍처: 상태 머신 기반 디코더 + EOF/에러 안전 처리

use ffmpeg_next as ffmpeg;
use std::path::Path;

/// 비디오 프레임 데이터
#[derive(Debug, Clone)]
pub struct Frame {
    pub width: u32,
    pub height: u32,
    pub format: PixelFormat,
    pub data: Vec<u8>,
    pub timestamp_ms: i64,
}

/// 픽셀 포맷
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PixelFormat {
    RGBA,
    RGB,
    YUV420P,
}

/// 디코더 상태 머신
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum DecoderState {
    Ready,          // 정상 동작 가능
    EndOfStream,    // 파일 끝 도달 (seek으로 복구 가능)
    Error,          // 복구 불가능한 에러
}

/// 디코딩 결과 (에러와 "프레임 없음"을 구분)
pub enum DecodeResult {
    /// 정상 프레임
    Frame(Frame),
    /// 프레임 스킵됨 (디코딩 실패했지만 계속 가능, 이전 프레임 유지)
    FrameSkipped,
    /// EOF 도달 + 마지막 성공 프레임 반환
    EndOfStream(Frame),
    /// EOF 도달 + 사용 가능한 프레임 없음
    EndOfStreamEmpty,
}

/// 비디오 디코더 (ffmpeg-next, 상태 머신 기반)
pub struct Decoder {
    input_ctx: ffmpeg::format::context::Input,
    video_stream_index: usize,
    decoder: ffmpeg::codec::decoder::Video,
    scaler: ffmpeg::software::scaling::Context,
    width: u32,
    height: u32,
    fps: f64,
    duration_ms: i64,
    last_timestamp_ms: i64,
    is_hardware: bool,
    state: DecoderState,
    /// 마지막 성공 디코딩 프레임 (EOF/에러 시 fallback용)
    last_decoded_frame: Option<Frame>,
    /// Forward decode 임계값 (ms)
    /// - 기본값: frame_duration * 2 (프리뷰 재생용)
    /// - 썸네일 세션: 10000ms (GOP 내 불필요한 seek 방지)
    /// - 현재 위치에서 이 범위 내의 미래 timestamp는 seek 없이 forward decode
    forward_threshold_ms: i64,
    /// EOF가 발생한 timestamp (ms) — 이 이후 timestamp에 대해 seek+decode 반복 방지
    /// 역방향 seek 시 자동 초기화
    eof_timestamp_ms: Option<i64>,
    /// Export용 YUV420P 직접 출력 모드 (RGBA 변환 건너뜀)
    /// true: 디코더 → YUV420P → 인코더 (색공간 변환 없이 최고 품질)
    /// false: 디코더 → RGBA → 프리뷰/썸네일/인코더
    yuv_output: bool,
}

impl Decoder {
    /// Decoder 생성 (Multi-threading 최적화)
    fn try_create_decoder(
        _codec_id: ffmpeg::codec::Id,
        codec_params: ffmpeg::codec::Parameters,
    ) -> Result<(ffmpeg::codec::decoder::Video, bool), String> {
        // Create decoder context
        let mut context = ffmpeg::codec::context::Context::from_parameters(codec_params)
            .map_err(|e| format!("Failed to create context: {}", e))?;

        // OPTIMIZATION: Multi-threading (디코더당 최대 4스레드)
        // 960x540에서 4스레드 이상은 수확체감. 트랜지션 시 2개 디코더 동시 구동 →
        // 전체 코어(8~16) 사용하면 스레드 경합으로 오히려 느려짐
        if let Ok(parallelism) = std::thread::available_parallelism() {
            let thread_count = parallelism.get().min(4);
            context.set_threading(ffmpeg::threading::Config {
                kind: ffmpeg::threading::Type::Frame,
                count: thread_count,
            });
        }

        // Open decoder
        let decoder = context
            .decoder()
            .video()
            .map_err(|e| format!("Failed to get video decoder: {}", e))?;

        // Hardware acceleration is set at input level (input_with_dictionary)
        // We'll detect it based on decoder format later
        Ok((decoder, false))  // is_hardware will be updated based on actual usage
    }

    /// 비디오 파일 열기 (프리뷰용 960x540 고정 해상도)
    pub fn open(file_path: &Path) -> Result<Self, String> {
        Self::open_internal(file_path, 960, 540, false, false)
    }

    /// 비디오 파일 열기 (커스텀 출력 해상도 지정)
    /// 썸네일 세션에서는 직접 썸네일 크기로 디코딩하여 불필요한 다운스케일 방지
    pub fn open_with_resolution(file_path: &Path, target_width: u32, target_height: u32) -> Result<Self, String> {
        Self::open_internal(file_path, target_width, target_height, false, false)
    }

    /// Export용 고품질 디코더 (YUV420P 직접 출력 + LANCZOS 리사이즈)
    /// RGBA 변환을 건너뛰어 색공간 변환 손실 제거
    pub fn open_for_export(file_path: &Path, target_width: u32, target_height: u32) -> Result<Self, String> {
        Self::open_internal(file_path, target_width, target_height, true, true)
    }

    /// 내부 디코더 생성
    /// - high_quality: LANCZOS(Export) vs FAST_BILINEAR(프리뷰)
    /// - yuv_output: YUV420P 직접 출력(Export) vs RGBA(프리뷰)
    fn open_internal(file_path: &Path, target_width: u32, target_height: u32, high_quality: bool, yuv_output: bool) -> Result<Self, String> {
        ffmpeg::init().map_err(|e| format!("FFmpeg init failed: {}", e))?;

        // 1차 시도: 기본 오픈
        // 2차 시도: moov atom이 파일 끝에 있는 경우 (카메라 녹화본 등) — probesize 확장
        let input_ctx = ffmpeg::format::input(&file_path)
            .or_else(|_| {
                let mut opts = ffmpeg::Dictionary::new();
                opts.set("probesize", "100000000");   // 100MB
                opts.set("analyzeduration", "30000000"); // 30초
                ffmpeg::format::input_with_dictionary(&file_path, opts)
            })
            .map_err(|e| format!("Failed to open file: {}", e))?;

        let video_stream = input_ctx
            .streams()
            .best(ffmpeg::media::Type::Video)
            .ok_or("No video stream found")?;

        let video_stream_index = video_stream.index();
        let codec_params = video_stream.parameters();
        let codec_id = codec_params.id();

        let (decoder, is_hardware) = Self::try_create_decoder(codec_id, codec_params)?;

        let src_width = decoder.width();
        let src_height = decoder.height();

        let decode_width = target_width;
        let decode_height = target_height;

        let fps = f64::from(video_stream.avg_frame_rate());

        let duration_ms = if video_stream.duration() > 0 {
            let time_base = video_stream.time_base();
            (video_stream.duration() * i64::from(time_base.numerator()) * 1000)
                / i64::from(time_base.denominator())
        } else if input_ctx.duration() > 0 {
            input_ctx.duration() / 1000
        } else {
            0
        };

        // Export: LANCZOS (최고 품질), 프리뷰: FAST_BILINEAR (속도 우선)
        let scaler_flags = if high_quality {
            ffmpeg::software::scaling::Flags::LANCZOS
        } else {
            ffmpeg::software::scaling::Flags::FAST_BILINEAR
        };

        // YUV 직접 출력: 색공간 변환 없이 YUV420P로 리사이즈만
        // RGBA 출력: 프리뷰/썸네일용 색공간 변환
        let output_pixel_format = if yuv_output {
            ffmpeg::format::Pixel::YUV420P
        } else {
            ffmpeg::format::Pixel::RGBA
        };

        let scaler = ffmpeg::software::scaling::Context::get(
            decoder.format(),
            src_width,
            src_height,
            output_pixel_format,
            decode_width,
            decode_height,
            scaler_flags,
        )
        .map_err(|e| format!("Failed to create scaler: {}", e))?;

        let _frame_duration_ms = (1000.0 / fps).max(1.0) as i64;

        Ok(Self {
            input_ctx,
            video_stream_index,
            decoder,
            scaler,
            width: decode_width,
            height: decode_height,
            fps,
            duration_ms,
            last_timestamp_ms: -1,
            is_hardware,
            state: DecoderState::Ready,
            last_decoded_frame: None,
            // [롤백] 스크럽 반응성을 위해 기본값은 낮게 유지 (100ms)
            // 재생 시 프리즈 방지는 set_playback_mode()를 통해 동적으로 값을 올려야 함
            forward_threshold_ms: 100,
            eof_timestamp_ms: None,
            yuv_output,
        })
    }

    /// Forward decode 임계값 설정
    /// 썸네일 세션에서 호출하여 GOP 내 불필요한 seek 방지
    pub fn set_forward_threshold(&mut self, threshold_ms: i64) {
        self.forward_threshold_ms = threshold_ms;
    }

    /// 비디오 정보 가져오기
    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    pub fn fps(&self) -> f64 {
        self.fps
    }

    pub fn duration_ms(&self) -> i64 {
        self.duration_ms
    }

    pub fn state(&self) -> DecoderState {
        self.state
    }

    /// 특정 시간의 프레임 디코딩 (상태 머신 기반)
    /// - 즉시 순차 (1프레임 이내): seek 없이, PTS 확인 없이 다음 프레임 반환
    /// - Forward decode (threshold 이내): seek 없이, PTS 확인하며 전진
    /// - 랜덤 접근 (threshold 초과 또는 역방향): seek + PTS 확인
    /// - EOF/에러: DecodeResult로 구분하여 안전 처리
    pub fn decode_frame(&mut self, timestamp_ms: i64) -> Result<DecodeResult, String> {
        // Error 상태에서는 마지막 프레임 반환
        if self.state == DecoderState::Error {
            return match &self.last_decoded_frame {
                Some(f) => Ok(DecodeResult::EndOfStream(f.clone())),
                None => Ok(DecodeResult::EndOfStreamEmpty),
            };
        }

        // EOF 캐싱: 이미 EOF에 도달한 위치 이후의 timestamp는 즉시 반환
        // (seek → 전체 패킷 읽기 → 다시 EOF 반복 방지)
        if let Some(eof_ts) = self.eof_timestamp_ms {
            if timestamp_ms >= eof_ts {
                return match &self.last_decoded_frame {
                    Some(f) => Ok(DecodeResult::EndOfStream(f.clone())),
                    None => Ok(DecodeResult::EndOfStreamEmpty),
                };
            } else {
                // 역방향 seek 시 EOF 마커 초기화
                self.eof_timestamp_ms = None;
            }
        }

        let frame_duration_ms = (1000.0 / self.fps).max(1.0) as i64;

        // 3단계 판정: 즉시순차 / forward decode / 랜덤접근
        let is_ahead = self.state == DecoderState::Ready
            && timestamp_ms >= self.last_timestamp_ms;
        let gap_ms = timestamp_ms - self.last_timestamp_ms;

        // 즉시 순차: 다음 프레임 (1프레임 이내 차이)
        let is_immediate = is_ahead && gap_ms <= frame_duration_ms * 2;
        // Forward decode: threshold 이내 전진 (seek 불필요, PTS 확인 필요)
        let is_forward = is_ahead && !is_immediate && gap_ms <= self.forward_threshold_ms;
        // 그 외: 랜덤 접근 (seek 필요)
        let needs_seek = !is_immediate && !is_forward;

        if needs_seek {
            if let Err(e) = self.seek(timestamp_ms) {
                eprintln!("Seek failed at {}ms: {}", timestamp_ms, e);
                return match &self.last_decoded_frame {
                    Some(_) => Ok(DecodeResult::FrameSkipped),
                    None => Ok(DecodeResult::EndOfStreamEmpty),
                };
            }
        }

        self.last_timestamp_ms = timestamp_ms;

        // PTS 확인 여부 결정:
        // - 즉시 순차: PTS 확인 불필요 (다음 프레임 즉시 반환)
        // - Forward decode: PTS 확인 필요 (목표 시간까지 전진)
        // - 랜덤 접근: PTS 확인 필요 (키프레임에서 목표까지 전진)
        let target_info = if is_immediate {
            None
        } else {
            let stream = self.input_ctx.stream(self.video_stream_index)
                .ok_or("Video stream not found")?;
            let tb = stream.time_base();
            let target_pts = (timestamp_ms * i64::from(tb.denominator()))
                / (i64::from(tb.numerator()) * 1000);
            let tolerance_pts = (frame_duration_ms * i64::from(tb.denominator()))
                / (i64::from(tb.numerator()) * 1000);
            Some((target_pts, tolerance_pts))
        };

        let mut decoded_frame: Option<ffmpeg::frame::Video> = None;

        // Step 1: 디코더 버퍼에서 프레임 확인
        loop {
            let mut frame = ffmpeg::frame::Video::empty();
            if self.decoder.receive_frame(&mut frame).is_err() {
                break;
            }
            if is_pts_at_target(target_info, &frame) {
                decoded_frame = Some(frame);
                break;
            }
        }

        // Step 2: 패킷 읽으며 디코딩 (목표 PTS 도달까지)
        let mut hit_eof = false;
        // [FIX] 목표 PTS에 도달하지 못하고 EOF가 발생할 경우를 대비해
        // 탐색 과정에서 본 가장 마지막 프레임을 저장
        let mut latest_seen_frame: Option<ffmpeg::frame::Video> = None;

        if decoded_frame.is_none() {
            let mut packet_count = 0;
            let mut packets_exhausted = true; // for 루프가 끝까지 소진되면 EOF

            for (stream, packet) in self.input_ctx.packets() {
                if stream.index() != self.video_stream_index {
                    continue;
                }

                // send_packet (EAGAIN 시 drain 후 재시도)
                if self.decoder.send_packet(&packet).is_err() {
                    loop {
                        let mut frame = ffmpeg::frame::Video::empty();
                        if self.decoder.receive_frame(&mut frame).is_err() { break; }
                        if is_pts_at_target(target_info, &frame) {
                            decoded_frame = Some(frame);
                            break;
                        }
                    }
                    if decoded_frame.is_some() { packets_exhausted = false; break; }
                    let _ = self.decoder.send_packet(&packet);
                }

                // 디코딩된 프레임 수신 (B-frame 재정렬 대응)
                loop {
                    let mut frame = ffmpeg::frame::Video::empty();
                    if self.decoder.receive_frame(&mut frame).is_err() { break; }
                    if is_pts_at_target(target_info, &frame) {
                        decoded_frame = Some(frame);
                        break;
                    }
                    
                    // 목표는 아니지만 유효한 프레임이므로 보관 (Proxy 불일치 대응)
                    latest_seen_frame = Some(frame);
                }

                if decoded_frame.is_some() { packets_exhausted = false; break; }

                packet_count += 1;
                if packet_count > 3000 {
                    // 안전장치: 3000패킷 소진 → FrameSkipped (에러가 아님)
                    // (타임라인 썸네일 생성 등 랜덤 접근 시 긴 GOP에서도
                    // 더 먼 위치까지 탐색할 수 있도록 상한을 상향 조정)
                    packets_exhausted = false;
                    break;
                }
            }

            // for 루프가 자연종료 = 패킷 소진 = EOF
            if packets_exhausted && decoded_frame.is_none() {
                hit_eof = true;
            }
        }

        // EOF 처리
        if hit_eof {
            self.state = DecoderState::EndOfStream;
            // EOF 위치 기록 → 이후 같은/더 먼 timestamp에서 seek+전패킷읽기 반복 방지
            self.eof_timestamp_ms = Some(timestamp_ms);

            // [FIX] 목표 프레임을 못 찾았지만 탐색 중 발견한 프레임이 있다면 그것을 반환
            // (예: Proxy가 원본보다 짧을 때, Proxy의 끝 프레임을 보여줌)
            if let Some(raw_frame) = latest_seen_frame {
                if let Ok(frame) = self.convert_frame(&raw_frame, timestamp_ms) {
                    self.last_decoded_frame = Some(frame.clone());
                    return Ok(DecodeResult::EndOfStream(frame));
                }
            }

            return match &self.last_decoded_frame {
                Some(f) => Ok(DecodeResult::EndOfStream(f.clone())),
                None => Ok(DecodeResult::EndOfStreamEmpty),
            };
        }

        // 프레임 디코딩 실패 (EOF가 아닌 경우) → FrameSkipped
        let raw_frame = match decoded_frame {
            Some(f) => f,
            None => return Ok(DecodeResult::FrameSkipped),
        };

        // 출력 프레임으로 변환 (RGBA 또는 YUV420P)
        let frame = self.convert_frame(&raw_frame, timestamp_ms)?;

        // 마지막 성공 프레임 저장 (EOF/에러 시 fallback)
        self.last_decoded_frame = Some(frame.clone());
        self.state = DecoderState::Ready;

        Ok(DecodeResult::Frame(frame))
    }

    /// 디코딩된 ffmpeg Video 프레임을 출력 형식으로 변환
    /// - yuv_output=false: RGBA (프리뷰/썸네일용)
    /// - yuv_output=true: YUV420P 직접 출력 (Export용 — 색공간 변환 손실 제거)
    /// bounds check 추가: FFmpeg이 손상된 프레임을 반환해도 panic 대신 Err 반환
    fn convert_frame(&mut self, raw_frame: &ffmpeg::frame::Video, timestamp_ms: i64) -> Result<Frame, String> {
        let mut scaled_frame = ffmpeg::frame::Video::empty();
        self.scaler.run(raw_frame, &mut scaled_frame)
            .map_err(|e| format!("Failed to scale frame: {}", e))?;

        if self.yuv_output {
            self.extract_yuv_frame(&scaled_frame, timestamp_ms)
        } else {
            self.extract_rgba_frame(&scaled_frame, timestamp_ms)
        }
    }

    /// RGBA 프레임 추출 (프리뷰/썸네일용)
    fn extract_rgba_frame(&self, frame: &ffmpeg::frame::Video, timestamp_ms: i64) -> Result<Frame, String> {
        let size = (self.width * self.height * 4) as usize;
        let mut data = vec![0u8; size];

        let src_data = frame.data(0);
        let linesize = frame.stride(0);

        // 안전성 검증
        let required_src_size = (self.height as usize - 1) * linesize + (self.width as usize * 4);
        if src_data.len() < required_src_size {
            return Err(format!(
                "Frame data too small: got {} bytes, need {} ({}x{}, stride={})",
                src_data.len(), required_src_size, self.width, self.height, linesize
            ));
        }

        if linesize < self.width as usize * 4 {
            return Err(format!(
                "Invalid stride: {} < {} (width * 4)",
                linesize, self.width as usize * 4
            ));
        }

        for y in 0..self.height as usize {
            let src_offset = y * linesize;
            let dst_offset = y * (self.width as usize * 4);
            let row_size = self.width as usize * 4;
            data[dst_offset..dst_offset + row_size]
                .copy_from_slice(&src_data[src_offset..src_offset + row_size]);
        }

        Ok(Frame {
            width: self.width,
            height: self.height,
            format: PixelFormat::RGBA,
            data,
            timestamp_ms,
        })
    }

    /// YUV420P 프레임 추출 (Export용 — 색공간 변환 없이 직접 전달)
    /// 데이터 레이아웃: [Y plane: w*h][U plane: w/2*h/2][V plane: w/2*h/2]
    fn extract_yuv_frame(&self, frame: &ffmpeg::frame::Video, timestamp_ms: i64) -> Result<Frame, String> {
        let w = self.width as usize;
        let h = self.height as usize;
        let y_size = w * h;
        let half_w = w / 2;
        let half_h = h / 2;
        let uv_size = half_w * half_h;
        let total = y_size + uv_size * 2;
        let mut data = vec![0u8; total];

        // Y plane
        let y_data = frame.data(0);
        let y_stride = frame.stride(0);
        for row in 0..h {
            let src_offset = row * y_stride;
            let dst_offset = row * w;
            if src_offset + w <= y_data.len() {
                data[dst_offset..dst_offset + w]
                    .copy_from_slice(&y_data[src_offset..src_offset + w]);
            }
        }

        // U plane
        let u_data = frame.data(1);
        let u_stride = frame.stride(1);
        for row in 0..half_h {
            let src_offset = row * u_stride;
            let dst_offset = y_size + row * half_w;
            if src_offset + half_w <= u_data.len() {
                data[dst_offset..dst_offset + half_w]
                    .copy_from_slice(&u_data[src_offset..src_offset + half_w]);
            }
        }

        // V plane
        let v_data = frame.data(2);
        let v_stride = frame.stride(2);
        for row in 0..half_h {
            let src_offset = row * v_stride;
            let dst_offset = y_size + uv_size + row * half_w;
            if src_offset + half_w <= v_data.len() {
                data[dst_offset..dst_offset + half_w]
                    .copy_from_slice(&v_data[src_offset..src_offset + half_w]);
            }
        }

        Ok(Frame {
            width: self.width,
            height: self.height,
            format: PixelFormat::YUV420P,
            data,
            timestamp_ms,
        })
    }

    /// 다음 프레임 디코딩
    pub fn decode_next_frame(&mut self) -> Result<Option<Frame>, String> {
        // TODO: 구현
        Ok(None)
    }

    /// 썸네일 프레임 생성 (작은 해상도로 디코딩)
    ///
    /// NOTE:
    /// - 기존 구현은 seek 후 "첫 프레임"만 가져오는 단순 로직이라,
    ///   GOP 구조에 따라 여러 timestamp가 모두 동일한 키프레임으로
    ///   떨어지는 문제가 있었다.
    /// - 여기서는 `decode_frame()`을 그대로 사용해 타임라인 렌더러와
    ///   동일한 시간 매핑을 따르고, 그 결과 RGBA 프레임을
    ///   thumb_width/height로 단순 축소(Nearest Neighbor)한다.
    pub fn generate_thumbnail(
        &mut self,
        timestamp_ms: i64,
        thumb_width: u32,
        thumb_height: u32,
    ) -> Result<Frame, String> {
        // 1) decode_frame으로 해당 timestamp의 RGBA 프레임 얻기
        let base_frame = match self.decode_frame(timestamp_ms)? {
            DecodeResult::Frame(f) => f,
            DecodeResult::EndOfStream(f) => f,
            DecodeResult::FrameSkipped => {
                match &self.last_decoded_frame {
                    Some(f) => f.clone(),
                    None => return Err("Failed to decode frame for thumbnail (FrameSkipped, no last frame)".into()),
                }
            }
            DecodeResult::EndOfStreamEmpty => {
                return Err("Failed to decode frame for thumbnail (EndOfStreamEmpty)".into());
            }
        };

        // 2) 크기가 이미 원하는 썸네일 크기라면 그대로 반환
        //    (open_with_resolution으로 열었으면 스케일러가 이미 thumb 크기)
        if base_frame.width == thumb_width && base_frame.height == thumb_height {
            return Ok(base_frame);
        }

        // 3) 크기 불일치 시 Nearest-Neighbor 다운스케일 (fallback)
        let src_w = base_frame.width as usize;
        let src_h = base_frame.height as usize;
        let dst_w = thumb_width as usize;
        let dst_h = thumb_height as usize;

        let mut data = vec![0u8; dst_w * dst_h * 4];

        for y in 0..dst_h {
            let src_y = y * src_h / dst_h;
            for x in 0..dst_w {
                let src_x = x * src_w / dst_w;

                let src_index = (src_y * src_w + src_x) * 4;
                let dst_index = (y * dst_w + x) * 4;

                data[dst_index..dst_index + 4]
                    .copy_from_slice(&base_frame.data[src_index..src_index + 4]);
            }
        }

        Ok(Frame {
            width: thumb_width,
            height: thumb_height,
            format: PixelFormat::RGBA,
            data,
            timestamp_ms,
        })
    }

    /// 특정 시간으로 seek (EOF/Error 상태에서 자동 복구)
    pub fn seek(&mut self, timestamp_ms: i64) -> Result<(), String> {
        // CRITICAL FIX: input_ctx.seek()는 내부적으로 avformat_seek_file(ctx, -1, ...)를 호출
        // stream_index = -1 이면 FFmpeg은 타임스탬프를 AV_TIME_BASE (마이크로초) 단위로 해석
        // 이전 코드는 stream time_base 단위로 변환 → seek 위치가 완전히 틀림
        // (예: 60초를 0.92초로 seek → 59초 전체를 forward decode → 선형 성능 저하)
        let timestamp_us = timestamp_ms * 1000; // ms → μs (AV_TIME_BASE)

        match self.input_ctx.seek(timestamp_us, ..timestamp_us) {
            Ok(_) => {
                self.decoder.flush();
                // seek 성공 → Ready 상태로 복구 (EOF/Error에서 복구)
                self.state = DecoderState::Ready;
                self.eof_timestamp_ms = None; // EOF 마커 초기화
                Ok(())
            }
            Err(e) => {
                // seek 실패 → flush 후 재시도 1회
                self.decoder.flush();
                match self.input_ctx.seek(timestamp_us, ..timestamp_us) {
                    Ok(_) => {
                        self.decoder.flush();
                        self.state = DecoderState::Ready;
                        Ok(())
                    }
                    Err(_) => {
                        self.state = DecoderState::Error;
                        Err(format!("Seek failed after retry: {}", e))
                    }
                }
            }
        }
    }
}

/// PTS가 목표에 도달했는지 확인 (모듈 레벨 함수 - borrow checker 충돌 방지)
/// target_info: None이면 순차 재생 → 항상 true (첫 프레임 즉시 수락)
/// target_info: Some((target_pts, tolerance_pts)) → PTS >= target - tolerance 이면 true
fn is_pts_at_target(target_info: Option<(i64, i64)>, frame: &ffmpeg::frame::Video) -> bool {
    match target_info {
        None => true, // 순차 재생: 다음 프레임 무조건 사용
        Some((target_pts, tolerance_pts)) => {
            match frame.pts() {
                Some(pts) => pts >= target_pts - tolerance_pts,
                None => true, // PTS 정보 없으면 수락
            }
        }
    }
}

// 실제 비디오 파일이 필요하므로 테스트는 주석 처리
#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    #[ignore] // 실제 비디오 파일 필요
    fn test_decoder_open() {
        let path = PathBuf::from("test.mp4");
        let decoder = Decoder::open(&path);
        assert!(decoder.is_ok());
    }

    #[test]
    #[ignore] // 실제 비디오 파일 필요
    fn test_decode_frame() {
        let path = PathBuf::from("test.mp4");
        let mut decoder = Decoder::open(&path).unwrap();

        let result = decoder.decode_frame(1000);
        assert!(result.is_ok());

        let frame = match result.unwrap() {
            DecodeResult::Frame(f) | DecodeResult::EndOfStream(f) => f,
            DecodeResult::FrameSkipped | DecodeResult::EndOfStreamEmpty => {
                panic!("Expected a decoded frame, got {:?}", decoder.state());
            }
        };

        assert_eq!(frame.timestamp_ms, 1000);
        assert!(!frame.data.is_empty());
    }

    #[test]
    fn test_decoder_with_real_file() {
        // 실제 비디오 파일로 테스트
        let path = PathBuf::from(r"C:\Users\USER\Videos\드론 대응 2.75인치 로켓 '비궁'으로 유도키트 개발, 사우디 기술협력 추진.mp4");

        if !path.exists() {
            println!("⚠️ Test video file not found, skipping test");
            return;
        }

        println!("\n=== Decoder Test ===");
        println!("📹 Opening video: {:?}", path);

        // 1. 디코더 열기
        let mut decoder = match Decoder::open(&path) {
            Ok(d) => {
                println!("✅ Decoder opened successfully");
                println!("   Resolution: {}x{}", d.width(), d.height());
                println!("   FPS: {:.2}", d.fps());
                println!("   Duration: {}ms", d.duration_ms());
                d
            }
            Err(e) => {
                panic!("❌ Failed to open decoder: {}", e);
            }
        };

        // 2. 프레임 디코딩 테스트 (0ms, 1000ms, 2000ms)
        let timestamps = [0i64, 1000, 2000];
        for timestamp in timestamps {
            println!("\n🎬 Decoding frame at {}ms...", timestamp);
            match decoder.decode_frame(timestamp) {
                Ok(result) => {
                    let frame = match result {
                        DecodeResult::Frame(f) | DecodeResult::EndOfStream(f) => f,
                        DecodeResult::FrameSkipped | DecodeResult::EndOfStreamEmpty => {
                            panic!("Expected a decoded frame at {}ms, got {:?}", timestamp, decoder.state());
                        }
                    };

                    println!("   ✅ Frame decoded: {}x{}", frame.width, frame.height);
                    println!("   Data size: {} bytes", frame.data.len());

                    // 첫 10픽셀 확인
                    let pixels: Vec<u8> = frame.data.iter().take(40).copied().collect();
                    println!("   First 10 pixels (RGBA): {:?}", pixels);

                    // 검증
                    assert_eq!(frame.width, decoder.width());
                    assert_eq!(frame.height, decoder.height());
                    assert_eq!(frame.data.len(), (frame.width * frame.height * 4) as usize);
                    assert_eq!(frame.format, PixelFormat::RGBA);
                }
                Err(e) => {
                    panic!("❌ Failed to decode frame at {}ms: {}", timestamp, e);
                }
            }
        }

        println!("\n✅ All decoder tests passed!");
    }
}

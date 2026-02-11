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

        // OPTIMIZATION: Multi-threading
        if let Ok(parallelism) = std::thread::available_parallelism() {
            let thread_count = parallelism.get();
            println!("   🔧 Enabling multi-threading: {} threads", thread_count);
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

    /// 비디오 파일 열기
    pub fn open(file_path: &Path) -> Result<Self, String> {
        // FFmpeg 초기화
        ffmpeg::init().map_err(|e| format!("FFmpeg init failed: {}", e))?;

        // 소프트웨어 디코딩 (멀티스레드 Frame threading으로 충분한 성능)
        // NOTE: hwaccel=cuda 옵션은 콘솔/테스트 환경에서 hang 유발하므로 제거
        let input_ctx = ffmpeg::format::input(&file_path)
            .map_err(|e| format!("Failed to open file: {}", e))?;

        // 비디오 스트림 찾기
        let video_stream = input_ctx
            .streams()
            .best(ffmpeg::media::Type::Video)
            .ok_or("No video stream found")?;

        let video_stream_index = video_stream.index();
        let codec_params = video_stream.parameters();
        let codec_id = codec_params.id();

        // OPTIMIZATION 2: Hardware acceleration 시도
        println!("🎬 Codec: {:?}", codec_id);

        let (decoder, is_hardware) = Self::try_create_decoder(codec_id, codec_params)?;

        // 비디오 정보 추출
        let src_width = decoder.width();
        let src_height = decoder.height();

        // OPTIMIZATION 1: 디코딩 해상도 절반으로 낮춤 (4배 속도 개선 예상)
        let decode_width = 960;
        let decode_height = 540;

        println!("🎬 Decoder opened: {}x{} (source) -> {}x{} (decode target)",
                 src_width, src_height, decode_width, decode_height);

        // FPS 계산
        let fps = f64::from(video_stream.avg_frame_rate());

        // Duration 계산 (ms)
        let duration_ms = if video_stream.duration() > 0 {
            let time_base = video_stream.time_base();
            (video_stream.duration() * i64::from(time_base.numerator()) * 1000)
                / i64::from(time_base.denominator())
        } else if input_ctx.duration() > 0 {
            input_ctx.duration() / 1000 // microseconds to milliseconds
        } else {
            0
        };

        // Scaler 생성 (YUV -> RGBA 변환 + 해상도 축소)
        // CRITICAL: output을 960x540으로 설정 (1920x1080의 1/4 픽셀)
        let scaler = ffmpeg::software::scaling::Context::get(
            decoder.format(),
            src_width,
            src_height,
            ffmpeg::format::Pixel::RGBA,
            decode_width,
            decode_height,
            ffmpeg::software::scaling::Flags::FAST_BILINEAR,  // BILINEAR -> FAST_BILINEAR (더 빠름)
        )
        .map_err(|e| format!("Failed to create scaler: {}", e))?;

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
        })
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
    /// - 순차 재생: seek 없이 다음 프레임 디코딩 (최적 경로)
    /// - 랜덤 접근(스크럽): seek → 키프레임에서 목표 PTS까지 디코딩 전진
    /// - EOF/에러: DecodeResult로 구분하여 재생 중단 없이 안전 처리
    pub fn decode_frame(&mut self, timestamp_ms: i64) -> Result<DecodeResult, String> {
        // Error 상태에서는 마지막 프레임 반환
        if self.state == DecoderState::Error {
            return match &self.last_decoded_frame {
                Some(f) => Ok(DecodeResult::EndOfStream(f.clone())),
                None => Ok(DecodeResult::EndOfStreamEmpty),
            };
        }

        let frame_duration_ms = (1000.0 / self.fps).max(1.0) as i64;
        let is_sequential = self.state == DecoderState::Ready
            && timestamp_ms >= self.last_timestamp_ms
            && timestamp_ms <= self.last_timestamp_ms + frame_duration_ms * 2;

        // EOF 상태에서 seek → Ready로 복구
        if !is_sequential {
            if let Err(e) = self.seek(timestamp_ms) {
                eprintln!("Seek failed at {}ms: {}", timestamp_ms, e);
                // seek 실패 시 마지막 프레임 반환 (재생 중단 방지)
                return match &self.last_decoded_frame {
                    Some(_) => Ok(DecodeResult::FrameSkipped),
                    None => Ok(DecodeResult::EndOfStreamEmpty),
                };
            }
        }

        self.last_timestamp_ms = timestamp_ms;

        // Seek 후: 목표 PTS까지 디코딩 전진에 필요한 정보 계산
        let target_info = if !is_sequential {
            let stream = self.input_ctx.stream(self.video_stream_index)
                .ok_or("Video stream not found")?;
            let tb = stream.time_base();
            let target_pts = (timestamp_ms * i64::from(tb.denominator()))
                / (i64::from(tb.numerator()) * 1000);
            let tolerance_pts = (frame_duration_ms * i64::from(tb.denominator()))
                / (i64::from(tb.numerator()) * 1000);
            Some((target_pts, tolerance_pts))
        } else {
            None // 순차 재생: PTS 확인 불필요, 다음 프레임 즉시 반환
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
                }

                if decoded_frame.is_some() { packets_exhausted = false; break; }

                packet_count += 1;
                if packet_count > 300 {
                    // 안전장치: 300패킷 소진 → FrameSkipped (에러가 아님)
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

        // RGBA 프레임으로 변환
        let frame = self.convert_to_rgba(&raw_frame, timestamp_ms)?;

        // 마지막 성공 프레임 저장 (EOF/에러 시 fallback)
        self.last_decoded_frame = Some(frame.clone());
        self.state = DecoderState::Ready;

        Ok(DecodeResult::Frame(frame))
    }

    /// 디코딩된 ffmpeg Video 프레임을 RGBA Frame으로 변환
    fn convert_to_rgba(&mut self, raw_frame: &ffmpeg::frame::Video, timestamp_ms: i64) -> Result<Frame, String> {
        let mut rgb_frame = ffmpeg::frame::Video::empty();
        self.scaler.run(raw_frame, &mut rgb_frame)
            .map_err(|e| format!("Failed to scale frame: {}", e))?;

        let size = (self.width * self.height * 4) as usize;
        let mut data = vec![0u8; size];

        let src_data = rgb_frame.data(0);
        let linesize = rgb_frame.stride(0);

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

    /// 다음 프레임 디코딩
    pub fn decode_next_frame(&mut self) -> Result<Option<Frame>, String> {
        // TODO: 구현
        Ok(None)
    }

    /// 썸네일 프레임 생성 (작은 해상도로 디코딩)
    pub fn generate_thumbnail(&mut self, timestamp_ms: i64, thumb_width: u32, thumb_height: u32) -> Result<Frame, String> {
        println!("📸 Generating thumbnail: timestamp={}ms, size={}x{}", timestamp_ms, thumb_width, thumb_height);

        // seek to timestamp
        self.seek(timestamp_ms)?;

        // 패킷 읽고 디코딩
        let mut decoded_frame: Option<ffmpeg::frame::Video> = None;

        for (stream, packet) in self.input_ctx.packets() {
            if stream.index() == self.video_stream_index {
                self.decoder.send_packet(&packet)
                    .map_err(|e| format!("Failed to send packet: {}", e))?;

                let mut frame = ffmpeg::frame::Video::empty();
                if self.decoder.receive_frame(&mut frame).is_ok() {
                    decoded_frame = Some(frame);
                    break;
                }
            }
        }

        let frame = decoded_frame.ok_or("Failed to decode thumbnail frame")?;

        // 썸네일용 scaler 생성 (작은 해상도)
        let mut thumb_scaler = ffmpeg::software::scaling::Context::get(
            self.decoder.format(),
            self.decoder.width(),
            self.decoder.height(),
            ffmpeg::format::Pixel::RGBA,
            thumb_width,
            thumb_height,
            ffmpeg::software::scaling::Flags::FAST_BILINEAR,
        )
        .map_err(|e| format!("Failed to create thumbnail scaler: {}", e))?;

        // RGBA 프레임으로 변환
        let mut rgb_frame = ffmpeg::frame::Video::empty();
        thumb_scaler.run(&frame, &mut rgb_frame)
            .map_err(|e| format!("Failed to scale thumbnail: {}", e))?;

        // 프레임 데이터 복사
        let size = (thumb_width * thumb_height * 4) as usize;
        let mut data = vec![0u8; size];

        let src_data = rgb_frame.data(0);
        let linesize = rgb_frame.stride(0);

        for y in 0..thumb_height as usize {
            let src_offset = y * linesize;
            let dst_offset = y * (thumb_width as usize * 4);
            let row_size = thumb_width as usize * 4;

            data[dst_offset..dst_offset + row_size]
                .copy_from_slice(&src_data[src_offset..src_offset + row_size]);
        }

        println!("✅ Thumbnail generated: {}x{}, data size={}", thumb_width, thumb_height, data.len());

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
        let stream = self.input_ctx.stream(self.video_stream_index)
            .ok_or("Video stream not found")?;

        let time_base = stream.time_base();

        // milliseconds to stream time base
        let timestamp = (timestamp_ms * i64::from(time_base.denominator()))
            / (i64::from(time_base.numerator()) * 1000);

        match self.input_ctx.seek(timestamp, ..timestamp) {
            Ok(_) => {
                self.decoder.flush();
                // seek 성공 → Ready 상태로 복구 (EOF/Error에서 복구)
                self.state = DecoderState::Ready;
                Ok(())
            }
            Err(e) => {
                // seek 실패 → flush 후 재시도 1회
                self.decoder.flush();
                match self.input_ctx.seek(timestamp, ..timestamp) {
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

        let frame = decoder.decode_frame(1000);
        assert!(frame.is_ok());

        let frame = frame.unwrap();
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
                Ok(frame) => {
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

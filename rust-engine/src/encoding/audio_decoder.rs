// 오디오 디코더 - FFmpeg으로 오디오 스트림을 f32 PCM으로 디코딩
// Export 오디오 믹싱 + 실시간 재생 겸용

use ffmpeg_next as ffmpeg;
use std::path::Path;

/// 오디오 디코더 (f32 stereo 48kHz 출력)
pub struct AudioDecoder {
    input_ctx: ffmpeg::format::context::Input,
    audio_stream_index: usize,
    decoder: ffmpeg::codec::decoder::Audio,
    resampler: ffmpeg::software::resampling::Context,
    sample_rate: u32,
    channels: u32,
    duration_ms: i64,
    /// 현재 디코딩된 위치 (ms)
    current_pos_ms: i64,
    /// 오디오 스트림 타임베이스 (PTS→ms 변환용)
    time_base_num: i32,
    time_base_den: i32,
    /// 입력 샘플레이트 (프레임 duration 계산용)
    input_sample_rate: u32,
    /// 초과 디코딩된 샘플 캐리 버퍼
    /// (프레임 경계 ≠ 청크 경계 → 초과분을 다음 decode_range에서 사용)
    leftover_samples: Vec<f32>,
}

/// 출력 포맷 상수
const OUTPUT_SAMPLE_RATE: u32 = 48000;
const OUTPUT_CHANNELS: u32 = 2;

/// seek 후 프레임 스킵 판정 결과
enum SkipResult {
    /// 전체 프레임 건너뜀 (리샘플링도 안 함)
    SkipEntire,
    /// 리샘플 후 앞부분 skip_count개 샘플 건너뜀
    Partial(usize),
    /// 스킵 불필요 (목표 시간 도달)
    NoSkip,
}

impl AudioDecoder {
    /// 오디오 파일 열기
    pub fn open(file_path: &Path) -> Result<Self, String> {
        ffmpeg::init().map_err(|e| format!("FFmpeg init failed: {}", e))?;

        let input_ctx = ffmpeg::format::input(file_path)
            .map_err(|e| format!("Failed to open audio file: {}", e))?;

        // 오디오 스트림 찾기
        let audio_stream = input_ctx
            .streams()
            .best(ffmpeg::media::Type::Audio)
            .ok_or("No audio stream found")?;

        let audio_stream_index = audio_stream.index();
        let codec_params = audio_stream.parameters();
        let time_base = audio_stream.time_base();
        let time_base_num = time_base.numerator();
        let time_base_den = time_base.denominator();

        // Duration 계산
        let duration_ms = if audio_stream.duration() > 0 {
            (audio_stream.duration() * i64::from(time_base_num) * 1000)
                / i64::from(time_base_den)
        } else if input_ctx.duration() > 0 {
            input_ctx.duration() / 1000 // AV_TIME_BASE(μs) → ms
        } else {
            0
        };

        // 디코더 생성
        let context = ffmpeg::codec::context::Context::from_parameters(codec_params)
            .map_err(|e| format!("Failed to create audio context: {}", e))?;
        let decoder = context.decoder().audio()
            .map_err(|e| format!("Failed to get audio decoder: {}", e))?;

        let input_sample_rate = decoder.rate();

        // 리샘플러 설정 (입력 포맷 → f32 stereo 48kHz)
        let resampler = ffmpeg::software::resampling::Context::get(
            decoder.format(),
            decoder.channel_layout(),
            decoder.rate(),
            ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Packed),
            ffmpeg::ChannelLayout::STEREO,
            OUTPUT_SAMPLE_RATE,
        )
        .map_err(|e| format!("Failed to create resampler: {}", e))?;

        Ok(Self {
            input_ctx,
            audio_stream_index,
            decoder,
            resampler,
            sample_rate: OUTPUT_SAMPLE_RATE,
            channels: OUTPUT_CHANNELS,
            duration_ms,
            current_pos_ms: 0,
            time_base_num,
            time_base_den,
            input_sample_rate,
            leftover_samples: Vec::new(),
        })
    }

    /// PTS를 밀리초로 변환 (오디오 스트림 타임베이스 기준)
    #[inline]
    fn pts_to_ms(&self, pts: i64) -> i64 {
        (pts * i64::from(self.time_base_num) * 1000) / i64::from(self.time_base_den)
    }

    /// seek 후 프레임을 건너뛸지 판정
    /// - 비디오 디코더의 PTS matching과 동일 역할
    /// - keyframe에서 목표까지의 불필요 샘플 제거
    fn check_skip(&self, frame: &ffmpeg::frame::Audio, target_ms: i64) -> SkipResult {
        let pts = match frame.pts() {
            Some(p) => p,
            None => return SkipResult::NoSkip, // PTS 없으면 보수적으로 스킵 종료
        };

        let frame_ms = self.pts_to_ms(pts);

        // 프레임 duration 계산 (입력 샘플레이트 기준)
        let frame_dur_ms = if self.input_sample_rate > 0 {
            (frame.samples() as i64 * 1000) / self.input_sample_rate as i64
        } else {
            0
        };
        let frame_end_ms = frame_ms + frame_dur_ms;

        if frame_end_ms <= target_ms {
            // 전체 프레임이 목표 전 → 리샘플링 없이 건너뜀
            SkipResult::SkipEntire
        } else if frame_ms < target_ms {
            // 부분 겹침 → 목표 전 샘플만 건너뜀 (출력 샘플레이트 기준)
            let skip_ms = (target_ms - frame_ms) as usize;
            let skip_count = skip_ms * self.sample_rate as usize
                * self.channels as usize / 1000;
            SkipResult::Partial(skip_count)
        } else {
            // 프레임이 목표 이후 → 스킵 종료, 전체 사용
            SkipResult::NoSkip
        }
    }

    /// 특정 시간 범위의 PCM 샘플 반환 (f32 interleaved stereo)
    /// samples 수 = (duration_ms / 1000) * sample_rate * channels
    ///
    /// 핵심: leftover_samples 캐리 버퍼로 프레임 경계 ≠ 청크 경계 문제 해결
    /// - 이전 청크에서 초과 디코딩된 샘플을 먼저 소비
    /// - 현재 청크에서 초과된 샘플을 다음 청크로 이월
    /// - 이 없으면 매 청크 경계에서 ~5-20ms 갭 → 연속 크래클링 발생
    ///
    /// duration_ms는 f64로 받아야 함 (30fps → 33.33ms):
    /// i64 truncation(33ms)하면 매 프레임 ~32 샘플 부족 → 30Hz 주기 클릭 노이즈
    pub fn decode_range(&mut self, start_ms: i64, duration_ms: f64) -> Result<Vec<f32>, String> {
        let num_samples = ((duration_ms / 1000.0) * self.sample_rate as f64) as usize
            * self.channels as usize;

        let duration_ms_i64 = duration_ms.ceil() as i64;

        // seek이 필요한 경우 (순차 접근이 아닌 경우)
        let did_seek = if start_ms < self.current_pos_ms
            || start_ms > self.current_pos_ms + 1000
        {
            self.seek(start_ms)?;
            true
        } else {
            false
        };

        let mut result = Vec::with_capacity(num_samples);

        // 이전 청크에서 초과 디코딩된 샘플 먼저 소비
        // (seek 시에는 leftover가 clear되므로 빈 상태)
        if !self.leftover_samples.is_empty() {
            let take = self.leftover_samples.len().min(num_samples);
            result.extend_from_slice(&self.leftover_samples[..take]);
            if take == self.leftover_samples.len() {
                self.leftover_samples.clear();
            } else {
                // leftover만으로도 충분한 경우 (드물지만 안전 처리)
                self.leftover_samples = self.leftover_samples[take..].to_vec();
            }
        }

        // leftover로 이미 충분하면 디코딩 불필요
        if result.len() >= num_samples {
            // 초과분 다시 leftover에 저장
            if result.len() > num_samples {
                self.leftover_samples = result[num_samples..].to_vec();
                result.truncate(num_samples);
            }
            self.current_pos_ms = start_ms + duration_ms_i64;
            return Ok(result);
        }

        // seek 직후에만 PTS 기반 스킵 활성화
        // (keyframe → target 사이의 불필요 샘플 제거)
        let mut skip_active = did_seek;

        while result.len() < num_samples {
            // Step 1: 디코더 버퍼에서 프레임 수신
            loop {
                let mut decoded = ffmpeg::frame::Audio::empty();
                if self.decoder.receive_frame(&mut decoded).is_err() {
                    break;
                }

                // seek 후: 목표 시간 전 샘플 건너뜀
                if skip_active {
                    match self.check_skip(&decoded, start_ms) {
                        SkipResult::SkipEntire => continue,
                        SkipResult::Partial(skip_count) => {
                            skip_active = false;
                            let samples = self.resample_frame(&decoded)?;
                            if skip_count < samples.len() {
                                result.extend_from_slice(&samples[skip_count..]);
                            }
                            if result.len() >= num_samples { break; }
                            continue;
                        }
                        SkipResult::NoSkip => {
                            skip_active = false;
                        }
                    }
                }

                let samples = self.resample_frame(&decoded)?;
                result.extend_from_slice(&samples);

                if result.len() >= num_samples {
                    break;
                }
            }

            if result.len() >= num_samples {
                break;
            }

            // Step 2: 새 패킷 읽기 (오디오 패킷을 찾을 때까지)
            let mut found_packet = false;
            for (stream, packet) in self.input_ctx.packets() {
                if stream.index() != self.audio_stream_index {
                    continue;
                }
                let _ = self.decoder.send_packet(&packet);
                found_packet = true;
                break;
            }

            if !found_packet {
                break; // EOF
            }
        }

        // 초과 샘플은 leftover에 보관 (잘라내지 않음!)
        // 부족하면 무음(0.0)으로 패딩
        if result.len() > num_samples {
            self.leftover_samples = result[num_samples..].to_vec();
            result.truncate(num_samples);
        } else {
            result.resize(num_samples, 0.0);
        }

        self.current_pos_ms = start_ms + duration_ms_i64;
        Ok(result)
    }

    /// 리샘플링: ffmpeg Audio 프레임 → f32 interleaved stereo
    fn resample_frame(&mut self, frame: &ffmpeg::frame::Audio) -> Result<Vec<f32>, String> {
        let mut resampled = ffmpeg::frame::Audio::empty();
        self.resampler.run(frame, &mut resampled)
            .map_err(|e| format!("Resample failed: {}", e))?;

        let data = resampled.data(0);
        let sample_count = resampled.samples() * self.channels as usize;
        let byte_count = sample_count * std::mem::size_of::<f32>();

        if data.len() < byte_count {
            return Ok(vec![0.0f32; sample_count]);
        }

        // f32 변환
        let mut samples = vec![0.0f32; sample_count];
        unsafe {
            std::ptr::copy_nonoverlapping(
                data.as_ptr(),
                samples.as_mut_ptr() as *mut u8,
                byte_count,
            );
        }

        Ok(samples)
    }

    /// 특정 시간으로 seek
    fn seek(&mut self, timestamp_ms: i64) -> Result<(), String> {
        // input_ctx.seek()은 stream_index=-1 → AV_TIME_BASE(μs) 단위 필요
        let ts_us = timestamp_ms * 1000;

        self.input_ctx.seek(ts_us, ..ts_us)
            .map_err(|e| format!("Audio seek failed: {}", e))?;
        self.decoder.flush();
        // seek 시 leftover 폐기 (이전 위치의 샘플이므로 무효)
        self.leftover_samples.clear();
        self.current_pos_ms = timestamp_ms;
        Ok(())
    }

    pub fn sample_rate(&self) -> u32 { self.sample_rate }
    pub fn channels(&self) -> u32 { self.channels }
    pub fn duration_ms(&self) -> i64 { self.duration_ms }
}

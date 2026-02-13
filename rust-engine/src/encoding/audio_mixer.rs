// 오디오 믹서 - 다중 오디오 클립을 하나의 PCM 스트림으로 합성
// Export 시 프레임 단위로 호출

use crate::encoding::audio_decoder::AudioDecoder;
use crate::timeline::AudioClip;
use std::collections::HashMap;

/// 출력 포맷 상수
const OUTPUT_SAMPLE_RATE: u32 = 48000;
const OUTPUT_CHANNELS: u32 = 2;

/// 오디오 믹서
pub struct AudioMixer {
    /// 파일별 디코더 캐시 (파일 경로 → AudioDecoder)
    decoder_cache: HashMap<String, AudioDecoder>,
}

impl AudioMixer {
    pub fn new() -> Self {
        Self {
            decoder_cache: HashMap::new(),
        }
    }

    /// 특정 시간 범위의 오디오 믹스 (모든 활성 클립 합산)
    /// - audio_clips: 현재 시간에 활성인 오디오 클립들
    /// - timestamp_ms: 타임라인 시간
    /// - duration_ms: 믹스할 시간 길이 (보통 1 프레임 ≈ 33ms)
    /// - 반환: f32 interleaved stereo PCM (sample_rate = 48kHz)
    pub fn mix_range(
        &mut self,
        audio_clips: &[AudioClip],
        timestamp_ms: i64,
        duration_ms: f64,
    ) -> Vec<f32> {
        let num_samples = ((duration_ms / 1000.0) * OUTPUT_SAMPLE_RATE as f64) as usize
            * OUTPUT_CHANNELS as usize;
        let mut mixed = vec![0.0f32; num_samples];

        if audio_clips.is_empty() {
            return mixed;
        }

        for clip in audio_clips {
            // 클립이 이 시간 범위와 겹치는지 확인
            if timestamp_ms >= clip.end_time_ms() || timestamp_ms + duration_ms as i64 <= clip.start_time_ms {
                continue;
            }

            // 원본 파일에서의 시간 계산
            let clip_offset = timestamp_ms - clip.start_time_ms;
            let source_start = clip.trim_start_ms + clip_offset;

            let file_path = clip.file_path.to_string_lossy().to_string();

            // 디코더 가져오기 (캐시에 없으면 생성)
            if !self.decoder_cache.contains_key(&file_path) {
                match AudioDecoder::open(&clip.file_path) {
                    Ok(decoder) => {
                        self.decoder_cache.insert(file_path.clone(), decoder);
                    }
                    Err(e) => {
                        eprintln!("[AUDIO_MIX] 디코더 열기 실패 {}: {}", file_path, e);
                        continue;
                    }
                }
            }

            let decoder = match self.decoder_cache.get_mut(&file_path) {
                Some(d) => d,
                None => continue,
            };

            // PCM 디코딩
            let samples = match decoder.decode_range(source_start, duration_ms as i64) {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("[AUDIO_MIX] 디코딩 실패 {}: {}", file_path, e);
                    continue;
                }
            };

            // 볼륨 적용 + 합산
            let volume = clip.volume;
            let len = mixed.len().min(samples.len());
            for i in 0..len {
                mixed[i] += samples[i] * volume;
            }
        }

        // 소프트 클리핑 (tanh) — 합산 시 1.0 초과 방지
        for sample in &mut mixed {
            if *sample > 1.0 || *sample < -1.0 {
                *sample = sample.tanh();
            }
        }

        mixed
    }

    /// 출력 샘플레이트
    pub fn sample_rate(&self) -> u32 { OUTPUT_SAMPLE_RATE }
    /// 출력 채널 수
    pub fn channels(&self) -> u32 { OUTPUT_CHANNELS }
}

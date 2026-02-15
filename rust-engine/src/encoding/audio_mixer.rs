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

/// Fade In/Out 볼륨 계산
fn calc_fade_volume(clip: &AudioClip, timestamp_ms: i64) -> f32 {
    let clip_time = timestamp_ms - clip.start_time_ms;
    let clip_end_offset = clip.duration_ms;
    let mut fade = 1.0f32;

    // Fade in
    if clip.fade_in_ms > 0 && clip_time < clip.fade_in_ms {
        fade *= clip_time as f32 / clip.fade_in_ms as f32;
    }

    // Fade out
    if clip.fade_out_ms > 0 {
        let remaining = clip_end_offset - clip_time;
        if remaining < clip.fade_out_ms {
            fade *= remaining as f32 / clip.fade_out_ms as f32;
        }
    }

    fade.clamp(0.0, 1.0)
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

            // speed 기반 원본 파일 시간 계산
            let clip_offset = timestamp_ms - clip.start_time_ms;
            let source_start = clip.trim_start_ms + (clip_offset as f64 * clip.speed) as i64;
            // speed에 따라 원본에서 더 많은/적은 구간 디코딩
            let source_duration = duration_ms * clip.speed;

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

            // PCM 디코딩 (source_duration으로 원본 구간 디코딩)
            let samples = match decoder.decode_range(source_start, source_duration) {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("[AUDIO_MIX] 디코딩 실패 {}: {}", file_path, e);
                    continue;
                }
            };

            // 볼륨 + 페이드 적용 + 합산
            let volume = clip.volume;
            let fade = calc_fade_volume(clip, timestamp_ms);
            let effective_volume = volume * fade;

            if clip.speed == 1.0 {
                // 속도 1.0: 디코딩된 샘플 직접 사용 (가장 효율적)
                let len = mixed.len().min(samples.len());
                for i in 0..len {
                    mixed[i] += samples[i] * effective_volume;
                }
            } else {
                // 속도 변경: 디코딩된 샘플을 리샘플링하여 출력 크기에 맞춤
                // speed=2.0 → samples 2배 많음 → 2개를 1개로 축소 (피치 변화)
                let out_len = mixed.len();
                let src_len = samples.len();
                if src_len == 0 { continue; }

                for i in 0..out_len {
                    // 선형 보간으로 리샘플링
                    let src_pos = i as f64 * clip.speed;
                    let src_idx = src_pos as usize;
                    let frac = src_pos - src_idx as f64;

                    if src_idx + 1 < src_len {
                        let sample = samples[src_idx] * (1.0 - frac as f32)
                            + samples[src_idx + 1] * frac as f32;
                        mixed[i] += sample * effective_volume;
                    } else if src_idx < src_len {
                        mixed[i] += samples[src_idx] * effective_volume;
                    }
                }
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

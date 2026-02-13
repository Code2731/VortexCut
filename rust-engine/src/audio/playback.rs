// 실시간 오디오 재생 엔진
// cpal로 오디오 출력, 링 버퍼로 샘플 공급, fill thread로 백그라운드 디코딩

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

use crate::encoding::audio_mixer::AudioMixer;
use crate::timeline::Timeline;

/// 출력 포맷 상수 (AudioDecoder/AudioMixer와 동일)
const SAMPLE_RATE: u32 = 48000;
const CHANNELS: u32 = 2;

/// 링 버퍼 용량: 1초 분량 (48000 * 2ch = 96000 f32 샘플)
const BUFFER_CAPACITY: usize = (SAMPLE_RATE * CHANNELS) as usize;

/// Fill thread 디코딩 청크 크기: 100ms
const DECODE_CHUNK_MS: f64 = 100.0;

/// 버퍼 채움 임계치: 50% 이하일 때 채움
const FILL_THRESHOLD: usize = BUFFER_CAPACITY / 2;

/// 선행 디코딩 청크 수: 3 * 100ms = 300ms
const PREFILL_CHUNKS: usize = 3;

/// 실시간 오디오 재생 엔진
pub struct AudioPlayback {
    /// cpal 출력 스트림 (Drop 시 자동 정지)
    _stream: cpal::Stream,
    /// 링 버퍼 (fill thread ↔ cpal callback 공유)
    buffer: Arc<Mutex<AudioRingBuffer>>,
    /// 재생 중 플래그
    is_playing: Arc<AtomicBool>,
    /// fill thread 종료 플래그
    cancelled: Arc<AtomicBool>,
    /// 백그라운드 디코딩 스레드
    fill_thread: Option<JoinHandle<()>>,
}

/// 링 버퍼
struct AudioRingBuffer {
    samples: VecDeque<f32>,
}

impl AudioRingBuffer {
    fn new() -> Self {
        Self {
            samples: VecDeque::with_capacity(BUFFER_CAPACITY),
        }
    }

    /// 샘플 추가 (fill thread에서 호출)
    fn push(&mut self, data: &[f32]) {
        // 용량 초과 시 오래된 샘플 제거
        let available = BUFFER_CAPACITY.saturating_sub(self.samples.len());
        if data.len() > available {
            let discard = data.len() - available;
            self.samples.drain(0..discard.min(self.samples.len()));
        }
        self.samples.extend(data);
    }

    /// 출력 버퍼에 직접 복사 (할당 없음 — cpal 실시간 callback용)
    /// VecDeque::as_slices()로 내부 슬라이스에서 직접 copy_from_slice
    /// → Vec 할당 제거, 이중 복사 제거, lock 시간 최소화
    fn fill_output(&mut self, output: &mut [f32]) {
        let available = self.samples.len().min(output.len());

        if available > 0 {
            // VecDeque는 내부적으로 원형 버퍼 → 최대 2개 연속 슬라이스
            let (front, back) = self.samples.as_slices();
            let mut written = 0;

            // front 슬라이스에서 복사
            let front_copy = front.len().min(available);
            if front_copy > 0 {
                output[..front_copy].copy_from_slice(&front[..front_copy]);
                written += front_copy;
            }

            // back 슬라이스에서 복사 (front만으로 부족한 경우)
            if written < available {
                let back_copy = (available - written).min(back.len());
                if back_copy > 0 {
                    output[written..written + back_copy]
                        .copy_from_slice(&back[..back_copy]);
                    written += back_copy;
                }
            }

            // 소비한 샘플 제거 (VecDeque head 포인터 이동, 할당 없음)
            self.samples.drain(0..written);
        }

        // 부족분은 무음으로 채움 (underrun 시 클릭 방지)
        for sample in &mut output[available..] {
            *sample = 0.0;
        }
    }

    fn len(&self) -> usize {
        self.samples.len()
    }
}

impl AudioPlayback {
    /// 오디오 재생 시작
    /// - timeline: Arc<Mutex<Timeline>> (타임라인 데이터)
    /// - start_time_ms: 재생 시작 위치 (타임라인 시간)
    pub fn start(
        timeline: Arc<Mutex<Timeline>>,
        start_time_ms: i64,
    ) -> Result<Self, String> {
        // cpal 디바이스 설정
        let host = cpal::default_host();
        let device = host.default_output_device()
            .ok_or("오디오 출력 디바이스를 찾을 수 없습니다")?;

        // 원하는 출력 포맷: f32 stereo 48kHz
        let config = cpal::StreamConfig {
            channels: CHANNELS as u16,
            sample_rate: cpal::SampleRate(SAMPLE_RATE),
            buffer_size: cpal::BufferSize::Default,
        };

        // 공유 상태
        let buffer = Arc::new(Mutex::new(AudioRingBuffer::new()));
        let is_playing = Arc::new(AtomicBool::new(true));
        let cancelled = Arc::new(AtomicBool::new(false));
        let prefill_done = Arc::new(AtomicBool::new(false));

        // Fill thread: 선행 디코딩 + 메인 루프
        let buffer_for_fill = Arc::clone(&buffer);
        let cancelled_for_fill = Arc::clone(&cancelled);
        let is_playing_for_fill = Arc::clone(&is_playing);
        let prefill_done_for_fill = Arc::clone(&prefill_done);

        let fill_thread = thread::spawn(move || {
            let mut mixer = AudioMixer::new();
            let mut current_time_ms = start_time_ms;
            let chunk_duration_ms = DECODE_CHUNK_MS;

            // Phase 1: 선행 디코딩 (300ms) — cpal 시작 전에 버퍼 채움
            let mut prefilled = 0;
            while prefilled < PREFILL_CHUNKS {
                if cancelled_for_fill.load(Ordering::Relaxed) {
                    prefill_done_for_fill.store(true, Ordering::Release);
                    return;
                }

                let audio_clips = match timeline.try_lock() {
                    Ok(tl) => tl.get_all_audio_sources_at_time(current_time_ms),
                    Err(_) => {
                        thread::sleep(std::time::Duration::from_millis(2));
                        continue; // 재시도 (prefilled 카운터 증가 안 함)
                    }
                };

                let samples = mixer.mix_range(
                    &audio_clips,
                    current_time_ms,
                    chunk_duration_ms,
                );

                if let Ok(mut buf) = buffer_for_fill.lock() {
                    buf.push(&samples);
                }

                current_time_ms += chunk_duration_ms as i64;
                prefilled += 1;
            }

            // 선행 디코딩 완료 신호
            prefill_done_for_fill.store(true, Ordering::Release);

            // Phase 2: 메인 루프 (cpal이 소비하는 만큼 계속 채움)
            while !cancelled_for_fill.load(Ordering::Relaxed) {
                if !is_playing_for_fill.load(Ordering::Relaxed) {
                    thread::sleep(std::time::Duration::from_millis(10));
                    continue;
                }

                let buffer_len = match buffer_for_fill.try_lock() {
                    Ok(buf) => buf.len(),
                    Err(_) => {
                        thread::sleep(std::time::Duration::from_millis(5));
                        continue;
                    }
                };

                if buffer_len > FILL_THRESHOLD {
                    thread::sleep(std::time::Duration::from_millis(10));
                    continue;
                }

                let audio_clips = match timeline.try_lock() {
                    Ok(tl) => tl.get_all_audio_sources_at_time(current_time_ms),
                    Err(_) => {
                        thread::sleep(std::time::Duration::from_millis(5));
                        continue;
                    }
                };

                let samples = mixer.mix_range(
                    &audio_clips,
                    current_time_ms,
                    chunk_duration_ms,
                );

                if let Ok(mut buf) = buffer_for_fill.lock() {
                    buf.push(&samples);
                }

                current_time_ms += chunk_duration_ms as i64;
            }
        });

        // 선행 디코딩 완료 대기 (최대 500ms)
        let wait_start = std::time::Instant::now();
        while !prefill_done.load(Ordering::Acquire) {
            if wait_start.elapsed() > std::time::Duration::from_millis(500) {
                break; // 타임아웃 → 그래도 진행
            }
            thread::sleep(std::time::Duration::from_millis(2));
        }

        // cpal 출력 스트림 (선행 디코딩된 버퍼에서 즉시 재생)
        let buffer_for_stream = Arc::clone(&buffer);
        let is_playing_for_stream = Arc::clone(&is_playing);

        let stream = device.build_output_stream(
            &config,
            move |data: &mut [f32], _: &cpal::OutputCallbackInfo| {
                if !is_playing_for_stream.load(Ordering::Relaxed) {
                    for sample in data.iter_mut() {
                        *sample = 0.0;
                    }
                    return;
                }

                // try_lock: 오디오 스레드는 절대 블로킹하면 안 됨
                // fill_output: VecDeque에서 직접 복사 → 힙 할당 없음
                match buffer_for_stream.try_lock() {
                    Ok(mut buf) => {
                        buf.fill_output(data);
                    }
                    Err(_) => {
                        // lock 실패 시 무음 (fill thread가 push 중)
                        for sample in data.iter_mut() {
                            *sample = 0.0;
                        }
                    }
                }
            },
            move |err| {
                eprintln!("[AUDIO_PLAYBACK] 스트림 에러: {}", err);
            },
            None,
        ).map_err(|e| format!("오디오 스트림 생성 실패: {}", e))?;

        stream.play().map_err(|e| format!("오디오 스트림 시작 실패: {}", e))?;

        Ok(Self {
            _stream: stream,
            buffer,
            is_playing,
            cancelled,
            fill_thread: Some(fill_thread),
        })
    }

    /// 재생 정지
    pub fn stop(&mut self) {
        self.cancelled.store(true, Ordering::Relaxed);
        self.is_playing.store(false, Ordering::Relaxed);

        // fill thread 종료 대기
        if let Some(handle) = self.fill_thread.take() {
            let _ = handle.join();
        }

        // 버퍼 클리어
        if let Ok(mut buf) = self.buffer.lock() {
            buf.samples.clear();
        }
    }

    /// 일시정지
    pub fn pause(&self) {
        self.is_playing.store(false, Ordering::Relaxed);
    }

    /// 재개
    pub fn resume(&self) {
        self.is_playing.store(true, Ordering::Relaxed);
    }
}

impl Drop for AudioPlayback {
    fn drop(&mut self) {
        self.stop();
    }
}

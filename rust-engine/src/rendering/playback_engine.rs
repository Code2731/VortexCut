//! PlaybackEngine - 재생 전용 백그라운드 프레임 프리페치
//!
//! 아키텍처:
//! - 별도 Renderer 인스턴스 (스크럽용 Renderer와 격리)
//! - 백그라운드 스레드가 FrameQueue에 미리 프레임 push
//! - UI 스레드는 queue에서 즉시 pop (디코딩 대기 없음)
//!
//! 성능 목표:
//! - 재생 프레임 드롭 70% → 0%
//! - 스크럽은 기존 Renderer 사용 (forward_threshold=100ms)
//! - 재생은 PlaybackEngine 사용 (forward_threshold=5000ms, 순차 디코딩)

use crate::timeline::Timeline;
use crate::rendering::{Renderer, RenderedFrame, FrameQueue};
use std::sync::{Arc, Mutex, atomic::{AtomicBool, AtomicI64, Ordering}};
use std::thread::{self, JoinHandle};
use std::time::Duration;
use std::io::Write;

/// 재생 엔진
pub struct PlaybackEngine {
    /// Timeline 참조 (Renderer와 공유)
    timeline: Arc<Mutex<Timeline>>,
    /// 프레임 큐 (링 버퍼, 최대 16프레임)
    frame_queue: Arc<Mutex<FrameQueue>>,
    /// 백그라운드 프리페치 스레드
    fill_thread: Option<JoinHandle<()>>,
    /// 스레드 취소 플래그
    cancelled: Arc<AtomicBool>,
    /// 재생 시작 시간 (ms)
    start_time_ms: Arc<AtomicI64>,
    /// 현재 프리페치 위치 (ms)
    current_prefetch_ms: Arc<AtomicI64>,
    /// C#이 마지막으로 요청한 시간 (오디오 마스터 시계 피드백)
    last_requested_ms: Arc<AtomicI64>,
}

impl PlaybackEngine {
    /// 새 재생 엔진 생성
    pub fn new(timeline: Arc<Mutex<Timeline>>) -> Self {
        Self {
            timeline,
            frame_queue: Arc::new(Mutex::new(FrameQueue::new())),
            fill_thread: None,
            cancelled: Arc::new(AtomicBool::new(false)),
            start_time_ms: Arc::new(AtomicI64::new(0)),
            current_prefetch_ms: Arc::new(AtomicI64::new(0)),
            last_requested_ms: Arc::new(AtomicI64::new(0)),
        }
    }

    /// 재생 시작 (백그라운드 프리페치 시작)
    pub fn start(&mut self, start_ms: i64) {
        // 워밍업 로그 파일 (eprintln은 GUI에서 안 보임!)
        let log_path = std::env::temp_dir().join("vortexcut_warmup.log");
        let mut warmup_log = std::fs::OpenOptions::new()
            .create(true).append(true)
            .open(&log_path).ok();

        macro_rules! wlog {
            ($($arg:tt)*) => {
                if let Some(ref mut f) = warmup_log {
                    let _ = writeln!(f, "{}", format!($($arg)*));
                    let _ = f.flush();
                }
            }
        }

        // 기존 스레드 정지
        self.stop();

        self.start_time_ms.store(start_ms, Ordering::SeqCst);
        self.current_prefetch_ms.store(start_ms, Ordering::SeqCst);
        self.last_requested_ms.store(start_ms, Ordering::SeqCst);
        self.cancelled.store(false, Ordering::SeqCst);

        // 큐 초기화 (unwrap 제거 - Rust best practice)
        if let Ok(mut queue) = self.frame_queue.lock() {
            queue.clear();
        }

        wlog!("[Warmup] PlaybackEngine.start({}) called", start_ms);

        // 백그라운드 스레드 시작
        let queue = self.frame_queue.clone();
        let timeline = self.timeline.clone();
        let cancelled = self.cancelled.clone();
        let current_prefetch = self.current_prefetch_ms.clone();
        let last_requested = self.last_requested_ms.clone();

        wlog!("[Warmup] Spawning fill_loop thread...");

        self.fill_thread = Some(thread::spawn(move || {
            Self::fill_loop(queue, timeline, cancelled, current_prefetch, last_requested);
        }));

        wlog!("[Warmup] fill_loop spawned - starting warmup wait (max 5000ms)");

        // 첫 프레임이 큐에 들어올 때까지 대기 (최대 5000ms)
        // 원거리 seek 시 FFmpeg 키프레임 탐색 + BLACK 프레임 재시도에 2~3초 소요 가능
        // 여기서 기다려야 C#이 큐에서 즉시 프레임을 가져갈 수 있음
        let warmup_start = std::time::Instant::now();
        let mut warmup_success = false;
        for i in 0..500 {
            if let Ok(q) = self.frame_queue.lock() {
                let qlen = q.len();
                if qlen > 0 {
                    let elapsed_ms = warmup_start.elapsed().as_millis();
                    wlog!("[Warmup] ✅ SUCCESS: {} frames ready in {}ms (iteration {})", qlen, elapsed_ms, i);
                    eprintln!("[PlaybackEngine] Warmup complete: {} frames ready in {}ms", qlen, elapsed_ms);
                    warmup_success = true;
                    break;
                }
            }
            thread::sleep(Duration::from_millis(10));
        }

        let elapsed_ms = warmup_start.elapsed().as_millis();
        if !warmup_success {
            wlog!("[Warmup] ⚠️ TIMEOUT after {}ms - queue still empty!", elapsed_ms);
            eprintln!("[PlaybackEngine] WARNING: Warmup timeout after {}ms!", elapsed_ms);
        }

        wlog!("[Warmup] start() returning to C#\n");
        eprintln!("[PlaybackEngine] Started from {}ms", start_ms);
    }

    /// 재생 정지 (백그라운드 스레드 종료)
    pub fn stop(&mut self) {
        if self.fill_thread.is_none() {
            return;
        }

        self.cancelled.store(true, Ordering::SeqCst);

        if let Some(handle) = self.fill_thread.take() {
            let _ = handle.join();
        }

        if let Ok(mut queue) = self.frame_queue.lock() {
            queue.clear();
        }
    }

    /// timestamp에 가장 가까운 프레임 조회 (디코딩 없음)
    /// tolerance=50ms (30fps 기준 ~1.5프레임)
    /// 호출 시 last_requested_ms 업데이트 → fill_loop에 소비 위치 피드백
    pub fn try_get_frame(&self, timestamp_ms: i64) -> Option<RenderedFrame> {
        self.last_requested_ms.store(timestamp_ms, Ordering::SeqCst);
        self.frame_queue.lock().ok()?.peek_nearest(timestamp_ms, 50)
    }

    /// 백그라운드 프리페치 루프
    /// - Timeline 순차 디코딩 (33ms 간격, 30fps 기준)
    /// - FrameQueue에 push (자동 evict)
    /// - last_requested_ms 기반 ahead 조절 (오디오 마스터 시계 추종)
    fn fill_loop(
        queue: Arc<Mutex<FrameQueue>>,
        timeline: Arc<Mutex<Timeline>>,
        cancelled: Arc<AtomicBool>,
        current_prefetch_ms: Arc<AtomicI64>,
        last_requested_ms: Arc<AtomicI64>,
    ) {
        // 진단 로그 파일 (GUI 앱에서 eprintln은 보이지 않음)
        let log_path = std::env::temp_dir().join("vortexcut_playback_engine.log");
        let mut log_file = std::fs::OpenOptions::new()
            .create(true).write(true).truncate(true)
            .open(&log_path).ok();

        macro_rules! pe_log {
            ($($arg:tt)*) => {
                if let Some(ref mut f) = log_file {
                    let _ = writeln!(f, "{}", format!($($arg)*));
                    let _ = f.flush();
                }
            }
        }

        pe_log!("[PlaybackEngine] fill_loop started, next_ms={}", current_prefetch_ms.load(Ordering::SeqCst));

        // 별도 Renderer 생성 (fill_loop 전용)
        let mut renderer = Renderer::new(timeline.clone());
        renderer.set_playback_mode(true); // forward_threshold=5000ms

        let mut next_ms = current_prefetch_ms.load(Ordering::SeqCst);
        let mut success_count: u64 = 0;
        let mut error_count: u64 = 0;

        // 첫 프레임 렌더 전 클립 진단 (어떤 클립이 렌더될지 확인)
        // let diag_clip = renderer.diag_clip_at(next_ms);
        pe_log!("[PlaybackEngine] Renderer created, starting loop from {}ms", next_ms);

        let mut loop_count: u64 = 0;
        let loop_start = std::time::Instant::now();

        while !cancelled.load(Ordering::SeqCst) {
            loop_count += 1;

            // 주기적 진행 로그 (첫 60초, 1초 간격)
            if loop_count % 90 == 0 && loop_count <= 1800 {
                let elapsed_sec = loop_start.elapsed().as_secs();
                let qlen = queue.lock().map_or(0, |q| q.len());
                let requested = last_requested_ms.load(Ordering::SeqCst);
                pe_log!("[PlaybackEngine] Loop #{}, {}s elapsed, success={}, errors={}, queueLen={}, next={}ms, requested={}ms, ahead={}ms",
                    loop_count, elapsed_sec, success_count, error_count, qlen, next_ms, requested, next_ms - requested);
            }

            // 오디오 마스터 시계 기반 ahead 조절
            // C#의 last_requested_ms를 기준으로 500ms 이상 앞서면 대기
            let requested = last_requested_ms.load(Ordering::SeqCst);
            let ahead = next_ms - requested;

            if ahead > 500 {
                // 충분히 앞서나감 → CPU 절약하며 대기
                thread::sleep(Duration::from_millis(10));
                continue;
            }

            // Mutex 상태 확인
            if queue.lock().is_err() {
                pe_log!("[PlaybackEngine] QUEUE MUTEX POISONED!");
                break;
            }

            // 프레임 렌더링 (순차 디코딩, forward_threshold=5000ms)
            let render_start = std::time::Instant::now();
            match renderer.render_frame(next_ms) {
                Ok(frame) => {
                    let render_dur_ms = render_start.elapsed().as_millis();

                    // FFmpeg 원거리 seek 아티팩트 검출: alpha=0x00 → BLACK 프레임
                    // 키프레임 seek 후 첫 디코딩 프레임이 빈 데이터일 수 있음
                    // 연속 BLACK 프레임 가능 → 최대 5회(~165ms) 재시도
                    let is_black = frame.data.len() >= 4 && frame.data[3] == 0x00;
                    if is_black {
                        pe_log!("[PlaybackEngine] BLACK frame at {}ms (alpha=0, renderTime={}ms), retrying up to 5 frames",
                            next_ms, render_dur_ms);

                        let mut retry_success = false;
                        for retry_offset in 1..=5 {
                            let retry_ms = next_ms + (retry_offset * 33);
                            match renderer.render_frame(retry_ms) {
                                Ok(retry_frame) => {
                                    if retry_frame.data.len() >= 4 && retry_frame.data[3] != 0x00 {
                                        pe_log!("[PlaybackEngine] Retry #{} at {}ms SUCCESS (alpha=0x{:02X})",
                                            retry_offset, retry_ms, retry_frame.data[3]);
                                        // 성공한 프레임을 큐에 push (원래 프레임 대신)
                                        if let Ok(mut q) = queue.lock() {
                                            q.push(retry_frame);
                                            current_prefetch_ms.store(retry_ms, Ordering::SeqCst);
                                        }
                                        success_count += 1;
                                        next_ms = retry_ms + 33; // 다음 프레임으로 이동
                                        retry_success = true;
                                        break;
                                    } else {
                                        pe_log!("[PlaybackEngine] Retry #{} at {}ms still BLACK", retry_offset, retry_ms);
                                    }
                                }
                                Err(e) => {
                                    pe_log!("[PlaybackEngine] Retry #{} ERROR: {}", retry_offset, e);
                                }
                            }
                        }

                        if !retry_success {
                            pe_log!("[PlaybackEngine] All 5 retries failed, skipping to +200ms");
                            next_ms += 200; // 5회 실패 시 크게 건너뜀
                        }
                        continue;
                    }

                    // 첫 3프레임: 첫 4바이트(RGBA 첫 픽셀) 캡처 (push 전, 소유권 이동 전)
                    let px_hex = if success_count < 3 && frame.data.len() >= 4 {
                        Some(format!("{:02X}{:02X}{:02X}{:02X}", frame.data[0], frame.data[1], frame.data[2], frame.data[3]))
                    } else {
                        None
                    };

                    if let Ok(mut q) = queue.lock() {
                        q.push(frame); // FrameQueue.push()가 자동 evict (max 16프레임)
                        current_prefetch_ms.store(next_ms, Ordering::SeqCst);
                    } else {
                        pe_log!("[PlaybackEngine] FAILED to lock queue for push at {}ms!", next_ms);
                    }

                    success_count += 1;

                    // 첫 10프레임 + 이후 30프레임마다(1초) 로그 출력
                    if success_count <= 10 || success_count % 30 == 0 {
                        let qlen = queue.lock().map_or(0, |q| q.len());
                        let px_str = px_hex.as_deref().map_or(String::new(), |h| format!(",px0={}", h));
                        pe_log!("[PlaybackEngine] frame #{} at {}ms, renderTime={}ms, queueLen={}, ahead={}ms{}",
                            success_count, next_ms, render_dur_ms, qlen, next_ms - requested, px_str);
                    }

                    // 렌더링이 너무 느리면 경고
                    if render_dur_ms > 50 && success_count <= 100 {
                        pe_log!("[PlaybackEngine] WARNING: Slow render at {}ms took {}ms!", next_ms, render_dur_ms);
                    }

                    next_ms += 33;
                }
                Err(e) => {
                    let render_dur_ms = render_start.elapsed().as_millis();
                    error_count += 1;
                    if error_count <= 10 {
                        pe_log!("[PlaybackEngine] ERROR #{} at {}ms after {}ms: {}",
                            error_count, next_ms, render_dur_ms, e);
                    }
                    next_ms += 33;
                }
            }
        }

        pe_log!("[PlaybackEngine] fill_loop ended: {} success, {} errors, elapsed={}s",
            success_count, error_count, loop_start.elapsed().as_secs());
    }

    /// 디버그: 큐 상태 조회
    pub fn queue_len(&self) -> usize {
        self.frame_queue.lock().ok().map_or(0, |q| q.len())
    }
}

impl Drop for PlaybackEngine {
    fn drop(&mut self) {
        self.stop();
    }
}

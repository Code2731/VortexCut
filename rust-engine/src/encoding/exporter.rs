// Export 작업 관리 - 백그라운드 스레드, 진행률, 취소
// ExportJob: 타임라인 → MP4 파일 내보내기 전체 흐름

use crate::encoding::encoder::VideoEncoder;
use crate::rendering::Renderer;
use crate::timeline::Timeline;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};

/// Export 설정
pub struct ExportConfig {
    pub output_path: String,
    pub width: u32,
    pub height: u32,
    pub fps: f64,
    pub crf: u32,
}

/// Export 작업 핸들 (C#에서 폴링으로 상태 확인)
pub struct ExportJob {
    /// 진행률 (0~100)
    progress: Arc<AtomicU32>,
    /// 취소 플래그
    cancelled: Arc<AtomicBool>,
    /// 완료 플래그
    finished: Arc<AtomicBool>,
    /// 에러 메시지 (있으면 실패)
    error: Arc<Mutex<Option<String>>>,
}

impl ExportJob {
    /// Export 시작 (백그라운드 스레드에서 실행)
    pub fn start(timeline: Arc<Mutex<Timeline>>, config: ExportConfig) -> Self {
        let progress = Arc::new(AtomicU32::new(0));
        let cancelled = Arc::new(AtomicBool::new(false));
        let finished = Arc::new(AtomicBool::new(false));
        let error: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));

        let p = progress.clone();
        let c = cancelled.clone();
        let f = finished.clone();
        let e = error.clone();

        std::thread::spawn(move || {
            let result = Self::export_thread(timeline, &config, &p, &c);
            match result {
                Ok(()) => {
                    p.store(100, Ordering::SeqCst);
                    eprintln!("[EXPORT] 완료: {}", config.output_path);
                }
                Err(msg) => {
                    if let Ok(mut err) = e.lock() {
                        *err = Some(msg.clone());
                    }
                    eprintln!("[EXPORT] 에러: {}", msg);
                }
            }
            f.store(true, Ordering::SeqCst);
        });

        Self { progress, cancelled, finished, error }
    }

    /// Export 메인 루프 (백그라운드 스레드)
    fn export_thread(
        timeline: Arc<Mutex<Timeline>>,
        config: &ExportConfig,
        progress: &AtomicU32,
        cancelled: &AtomicBool,
    ) -> Result<(), String> {
        eprintln!(
            "[EXPORT] 시작: {}x{} @ {}fps, CRF={}, 출력={}",
            config.width, config.height, config.fps, config.crf, config.output_path
        );

        // 1. 타임라인 duration 가져오기
        let duration_ms = {
            let tl = timeline.lock().map_err(|e| format!("Timeline lock failed: {}", e))?;
            tl.duration_ms()
        };

        if duration_ms <= 0 {
            return Err("타임라인이 비어있습니다".to_string());
        }

        eprintln!("[EXPORT] 타임라인 길이: {}ms", duration_ms);

        // 2. Export용 전용 Renderer 생성 (프리뷰와 격리)
        let mut renderer = Renderer::new_for_export(
            timeline.clone(),
            config.width,
            config.height,
        );

        // 3. VideoEncoder 생성
        let mut encoder = VideoEncoder::new(
            &config.output_path,
            config.width,
            config.height,
            config.fps,
            config.crf,
        )?;

        // 4. 헤더 작성
        encoder.write_header()?;

        // 5. 프레임 단위로 렌더링 → 인코딩
        let frame_duration_ms = 1000.0 / config.fps;
        let total_frames = ((duration_ms as f64) / frame_duration_ms).ceil() as i64;
        let mut frame_index: i64 = 0;

        eprintln!("[EXPORT] 총 프레임: {}", total_frames);

        loop {
            // 취소 확인
            if cancelled.load(Ordering::SeqCst) {
                eprintln!("[EXPORT] 취소됨 (frame {}/{})", frame_index, total_frames);
                // 인코더 정리 (불완전 파일)
                let _ = encoder.finish();
                return Err("Export가 취소되었습니다".to_string());
            }

            let timestamp_ms = (frame_index as f64 * frame_duration_ms) as i64;
            if timestamp_ms >= duration_ms {
                break;
            }

            // 프레임 렌더링
            let frame = renderer.render_frame(timestamp_ms)
                .map_err(|e| format!("렌더링 실패 ({}ms): {}", timestamp_ms, e))?;

            // 인코딩 (해상도가 다르면 스킵하지 않고 그대로 전달 — encoder가 검증)
            encoder.encode_frame(&frame.data, frame.width, frame.height)?;

            // 진행률 업데이트
            let pct = ((frame_index + 1) * 100 / total_frames).min(99) as u32;
            progress.store(pct, Ordering::SeqCst);

            frame_index += 1;

            // 매 300프레임(~10초)마다 로그
            if frame_index % 300 == 0 {
                eprintln!("[EXPORT] 진행: {}/{} ({}%)", frame_index, total_frames, pct);
            }
        }

        // 6. 인코딩 완료 (flush + trailer)
        encoder.finish()?;

        Ok(())
    }

    /// 진행률 가져오기 (0~100)
    pub fn get_progress(&self) -> u32 {
        self.progress.load(Ordering::SeqCst)
    }

    /// 취소 요청
    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::SeqCst);
    }

    /// 완료 여부
    pub fn is_finished(&self) -> bool {
        self.finished.load(Ordering::SeqCst)
    }

    /// 에러 메시지 가져오기 (None이면 성공 또는 진행 중)
    pub fn get_error(&self) -> Option<String> {
        self.error.lock().ok().and_then(|e| e.clone())
    }
}

// Export 작업 관리 - 백그라운드 스레드, 진행률, 취소
// ExportJob: 타임라인 → MP4 파일 내보내기 전체 흐름
// 비디오 (H.264) + 오디오 (AAC) 동시 인코딩

use crate::encoding::encoder::{VideoEncoder, EncoderType};
use crate::encoding::audio_mixer::AudioMixer;
use crate::rendering::Renderer;
use crate::subtitle::overlay::{SubtitleOverlayList, blend_overlay_rgba, yuv420p_to_rgba, rgba_to_yuv420p};
use crate::timeline::Timeline;
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};

/// Export 설정
pub struct ExportConfig {
    pub output_path: String,
    pub width: u32,
    pub height: u32,
    pub fps: f64,
    pub crf: u32,
    pub encoder_type: u32,  // 0=Auto, 1=Software, 2=NVENC, 3=QSV, 4=AMF
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
        Self::start_with_subtitles(timeline, config, None)
    }

    /// Export 시작 (자막 포함)
    pub fn start_with_subtitles(
        timeline: Arc<Mutex<Timeline>>,
        config: ExportConfig,
        subtitles: Option<SubtitleOverlayList>,
    ) -> Self {
        let progress = Arc::new(AtomicU32::new(0));
        let cancelled = Arc::new(AtomicBool::new(false));
        let finished = Arc::new(AtomicBool::new(false));
        let error: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));

        let p = progress.clone();
        let c = cancelled.clone();
        let f = finished.clone();
        let e = error.clone();

        std::thread::spawn(move || {
            let result = Self::export_thread(timeline, &config, &p, &c, subtitles.as_ref());
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

    /// 비ASCII 경로(한글 등) 안전 처리
    fn safe_encoder_path(output_path: &str) -> (String, bool) {
        if output_path.is_ascii() {
            return (output_path.to_string(), false);
        }

        let final_path = Path::new(output_path);
        let ext = final_path
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or("mp4");

        let temp_name = format!("vortex_export_{}.{}", std::process::id(), ext);
        let temp_path = std::env::temp_dir().join(&temp_name);

        let temp_str = temp_path.to_string_lossy().to_string();
        if temp_str.is_ascii() {
            eprintln!("[EXPORT] 비ASCII 경로 → 임시 경로: {}", temp_str);
            return (temp_str, true);
        }

        if let Some(drive) = output_path.chars().next() {
            if output_path.chars().nth(1) == Some(':') {
                let root_temp = format!("{}:\\{}", drive, temp_name);
                eprintln!("[EXPORT] TEMP도 비ASCII → 드라이브 루트: {}", root_temp);
                return (root_temp, true);
            }
        }

        (output_path.to_string(), false)
    }

    /// 파일 이동 (같은 드라이브면 rename, 다른 드라이브면 copy+delete)
    fn move_file(src: &str, dst: &str) -> Result<(), String> {
        let dst_path = Path::new(dst);

        if let Some(parent) = dst_path.parent() {
            std::fs::create_dir_all(parent)
                .map_err(|e| format!("출력 디렉토리 생성 실패: {}", e))?;
        }

        if std::fs::rename(src, dst).is_ok() {
            return Ok(());
        }

        std::fs::copy(src, dst)
            .map_err(|e| format!("파일 복사 실패: {}", e))?;
        let _ = std::fs::remove_file(src);

        Ok(())
    }

    /// Export 메인 루프 (백그라운드 스레드)
    fn export_thread(
        timeline: Arc<Mutex<Timeline>>,
        config: &ExportConfig,
        progress: &AtomicU32,
        cancelled: &AtomicBool,
        subtitles: Option<&SubtitleOverlayList>,
    ) -> Result<(), String> {
        eprintln!(
            "[EXPORT] 시작: {}x{} @ {}fps, CRF={}, 출력={}",
            config.width, config.height, config.fps, config.crf, config.output_path
        );

        // 0. 출력 디렉토리 생성
        let output_path = Path::new(&config.output_path);
        if let Some(parent) = output_path.parent() {
            std::fs::create_dir_all(parent)
                .map_err(|e| format!("출력 디렉토리 생성 실패: {}", e))?;
        }

        // 1. 타임라인 duration 가져오기
        let duration_ms = {
            let tl = timeline.lock().map_err(|e| format!("Timeline lock failed: {}", e))?;
            tl.duration_ms()
        };

        if duration_ms <= 0 {
            return Err("타임라인이 비어있습니다".to_string());
        }

        eprintln!("[EXPORT] 타임라인 길이: {}ms", duration_ms);

        // 2. Export용 전용 Renderer + AudioMixer 생성
        let mut renderer = Renderer::new_for_export(
            timeline.clone(),
            config.width,
            config.height,
        );
        let mut audio_mixer = AudioMixer::new();

        // 3. 비ASCII 경로 처리
        let (encoder_path, needs_move) = Self::safe_encoder_path(&config.output_path);

        // 4. VideoEncoder 생성 (인코더 타입 전달)
        let enc_type = EncoderType::from_u32(config.encoder_type);
        let (mut encoder, encoder_path, needs_move) = match VideoEncoder::new(
            &encoder_path,
            config.width,
            config.height,
            config.fps,
            config.crf,
            enc_type,
        ) {
            Ok(enc) => (enc, encoder_path, needs_move),
            Err(e) if needs_move => {
                eprintln!("[EXPORT] 안전 경로 실패 ({}), 원본 경로로 재시도", e);
                let enc = VideoEncoder::new(
                    &config.output_path,
                    config.width,
                    config.height,
                    config.fps,
                    config.crf,
                    enc_type,
                ).map_err(|e2| format!("인코더 생성 실패: {} (재시도: {})", e, e2))?;
                (enc, config.output_path.clone(), false)
            }
            Err(e) => return Err(format!("인코더 생성 실패: {}", e)),
        };

        // 5. AAC 오디오 인코더 초기화 (48kHz stereo, 192kbps)
        match encoder.init_audio(48000, 2, 192000) {
            Ok(()) => eprintln!("[EXPORT] 오디오 인코더 초기화 성공"),
            Err(e) => {
                // 오디오 인코더 실패해도 비디오만이라도 Export 계속
                eprintln!("[EXPORT] 오디오 인코더 초기화 실패 (비디오만 Export): {}", e);
            }
        }

        // 6. 헤더 작성 (비디오+오디오 스트림 모두 등록 후)
        encoder.write_header()?;

        // 7. 프레임 단위로 렌더링 → 인코딩
        let frame_duration_ms = 1000.0 / config.fps;
        let total_frames = ((duration_ms as f64) / frame_duration_ms).ceil() as i64;
        let mut frame_index: i64 = 0;

        eprintln!("[EXPORT] 총 프레임: {}", total_frames);

        loop {
            // 취소 확인
            if cancelled.load(Ordering::SeqCst) {
                eprintln!("[EXPORT] 취소됨 (frame {}/{})", frame_index, total_frames);
                let _ = encoder.finish();
                if needs_move {
                    let _ = std::fs::remove_file(&encoder_path);
                }
                return Err("Export가 취소되었습니다".to_string());
            }

            let timestamp_ms = (frame_index as f64 * frame_duration_ms) as i64;
            if timestamp_ms >= duration_ms {
                break;
            }

            // 비디오 프레임 렌더링
            let frame = renderer.render_frame(timestamp_ms)
                .map_err(|e| format!("렌더링 실패 ({}ms): {}", timestamp_ms, e))?;

            if frame_index == 0 {
                eprintln!(
                    "[EXPORT] 첫 프레임: rendered={}x{}, encoder={}x{}, data={}bytes",
                    frame.width, frame.height,
                    encoder.width(), encoder.height(),
                    frame.data.len()
                );
            }

            // 자막 오버레이 합성 (있을 때만 RGBA 경로)
            if let Some(overlay) = subtitles.and_then(|s| s.get_active(timestamp_ms)) {
                // 자막 프레임: YUV→RGBA 변환 → 알파 블렌딩 → RGBA 인코딩
                let mut rgba = if frame.is_yuv {
                    yuv420p_to_rgba(&frame.data, frame.width, frame.height)
                } else {
                    frame.data.clone()
                };
                blend_overlay_rgba(&mut rgba, frame.width, frame.height, overlay);
                // RGBA→YUV420P 변환 후 인코딩 (YUV 직접 경로 유지)
                let yuv = rgba_to_yuv420p(&rgba, frame.width, frame.height);
                encoder.encode_frame_yuv(&yuv, frame.width, frame.height)?;
            } else {
                // 자막 없는 프레임: 기존 직접 경로 (변환 손실 없음)
                if frame.is_yuv {
                    encoder.encode_frame_yuv(&frame.data, frame.width, frame.height)?;
                } else {
                    encoder.encode_frame(&frame.data, frame.width, frame.height)?;
                }
            }

            // 오디오 믹싱 + 인코딩
            let audio_clips = {
                let tl = timeline.lock()
                    .map_err(|e| format!("Timeline lock failed: {}", e))?;
                tl.get_all_audio_sources_at_time(timestamp_ms)
            };
            let audio_samples = audio_mixer.mix_range(
                &audio_clips,
                timestamp_ms,
                frame_duration_ms,
            );
            encoder.encode_audio_samples(&audio_samples)?;

            // 진행률 업데이트
            let pct = ((frame_index + 1) * 100 / total_frames).min(99) as u32;
            progress.store(pct, Ordering::SeqCst);

            frame_index += 1;

            // 매 300프레임(~10초)마다 로그
            if frame_index % 300 == 0 {
                eprintln!("[EXPORT] 진행: {}/{} ({}%)", frame_index, total_frames, pct);
            }
        }

        // 8. 인코딩 완료 (flush + trailer)
        encoder.finish()?;

        // 9. 임시 파일을 최종 경로로 이동 (비ASCII 경로)
        if needs_move {
            eprintln!("[EXPORT] 임시 파일 이동: {} → {}", encoder_path, config.output_path);
            Self::move_file(&encoder_path, &config.output_path)?;
        }

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

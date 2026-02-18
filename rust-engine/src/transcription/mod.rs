// Whisper 음성 인식 모듈
// whisper-rs 바인딩을 통한 자동 자막 생성
// Export 패턴(AtomicU32 progress, AtomicBool finished) 동일하게 적용

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};

/// 자막 세그먼트 (whisper 출력 단위)
pub struct TranscriptSegment {
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
}

/// 트랜스크립션 작업 핸들 (C#에서 폴링으로 상태 확인)
pub struct TranscriberJob {
    /// 진행률 0~100
    pub progress: Arc<AtomicU32>,
    /// 완료 플래그
    pub finished: Arc<AtomicBool>,
    /// 에러 메시지 (있으면 실패)
    pub error: Arc<Mutex<Option<String>>>,
    /// 완료된 세그먼트 목록
    pub segments: Arc<Mutex<Vec<TranscriptSegment>>>,
    /// 중단 요청 플래그 (C#에서 Cancel 시 true로 설정)
    pub abort_requested: Arc<AtomicBool>,
}

impl TranscriberJob {
    pub fn get_progress(&self) -> u32 {
        self.progress.load(Ordering::SeqCst)
    }

    pub fn is_finished(&self) -> bool {
        self.finished.load(Ordering::SeqCst)
    }

    pub fn get_error(&self) -> Option<String> {
        self.error.lock().ok()?.clone()
    }

    pub fn get_segments(&self) -> Vec<TranscriptSegment> {
        match self.segments.lock() {
            Ok(segs) => segs.iter().map(|s| TranscriptSegment {
                start_ms: s.start_ms,
                end_ms: s.end_ms,
                text: s.text.clone(),
            }).collect(),
            Err(_) => vec![],
        }
    }
}

/// FFmpeg으로 오디오를 16kHz mono f32 PCM으로 추출
/// AudioDecoder::open() 패턴 동일 적용
pub fn extract_audio_16k_mono(path: &str) -> Result<Vec<f32>, String> {
    use ffmpeg_next as ffmpeg;

    ffmpeg::init().map_err(|e| format!("FFmpeg 초기화 실패: {}", e))?;

    // Opus 디코더 내부 경고 억제 (AV_LOG_ERROR 레벨 메시지 → stderr 출력 방지)
    // avcodec_send_packet 내부에서 출력되므로 Rust 에러 핸들링으로는 막을 수 없음
    ffmpeg::util::log::set_level(ffmpeg::util::log::Level::Fatal);

    // 1차 시도: 기본 오픈
    // 2차 시도: moov atom이 파일 끝에 있는 경우 (카메라 녹화본 등) — probesize 확장
    let mut input_ctx = ffmpeg::format::input(&path)
        .or_else(|_| {
            let mut opts = ffmpeg::Dictionary::new();
            opts.set("probesize", "100000000");   // 100MB
            opts.set("analyzeduration", "30000000"); // 30초
            ffmpeg::format::input_with_dictionary(&path, opts)
        })
        .map_err(|e| format!("파일 열기 실패: {}", e))?;

    // 최적 오디오 스트림 탐색
    let audio_stream_index = {
        let stream = input_ctx
            .streams()
            .best(ffmpeg::media::Type::Audio)
            .ok_or_else(|| "오디오 스트림 없음".to_string())?;
        stream.index()
    };

    let codec_params = input_ctx
        .stream(audio_stream_index)
        .ok_or("스트림 없음")?
        .parameters();

    // 디코더 생성
    let ctx = ffmpeg::codec::context::Context::from_parameters(codec_params)
        .map_err(|e| format!("코덱 컨텍스트 생성 실패: {}", e))?;
    let mut decoder = ctx.decoder()
        .audio()
        .map_err(|e| format!("오디오 디코더 생성 실패: {}", e))?;

    // 리샘플러: Option으로 선언 — Opus는 첫 패킷 디코딩 후에야 실제 포맷 확정
    // 코덱 파라미터 기반 사전 생성 시 Opus에서 잘못된 채널 레이아웃/샘플레이트로 생성될 수 있음
    let mut resampler: Option<ffmpeg::software::resampling::Context> = None;

    let mut samples = Vec::<f32>::new();
    let mut resampled = ffmpeg::frame::Audio::empty();

    // 패킷 루프
    for (stream, packet) in input_ctx.packets() {
        if stream.index() != audio_stream_index {
            continue;
        }
        // 오류 패킷은 스킵 — flush하면 디코더 상태가 리셋되어 이후 패킷도 디코딩 불가
        if decoder.send_packet(&packet).is_err() {
            continue;
        }
        let mut decoded = ffmpeg::frame::Audio::empty();
        while decoder.receive_frame(&mut decoded).is_ok() {
            // 첫 번째 성공 프레임의 실제 포맷으로 리샘플러 초기화
            if resampler.is_none() {
                match ffmpeg::software::resampling::Context::get(
                    decoded.format(),
                    decoded.channel_layout(),
                    decoded.rate(),
                    ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Packed),
                    ffmpeg::ChannelLayout::MONO,
                    16000,
                ) {
                    Ok(r) => resampler = Some(r),
                    Err(e) => {
                        return Err(format!("리샘플러 생성 실패: {}", e));
                    }
                }
            }
            let r = resampler.as_mut().unwrap();
            if r.run(&decoded, &mut resampled).is_err() {
                continue;
            }
            if resampled.samples() == 0 {
                continue;
            }
            // data.len() 대신 samples() 사용 — FFmpeg linesize에 패딩 포함됨
            // padding 바이트가 f32로 해석되면 수조 단위 값 → whisper failed to encode
            let n = resampled.samples(); // 유효 샘플 수 (mono)
            let data = resampled.data(0);
            let f32_slice: &[f32] = unsafe {
                std::slice::from_raw_parts(data.as_ptr() as *const f32, n)
            };
            samples.extend_from_slice(f32_slice);
        }
    }

    // EOF 플러시
    decoder.send_eof().ok();
    let mut decoded = ffmpeg::frame::Audio::empty();
    while decoder.receive_frame(&mut decoded).is_ok() {
        if let Some(r) = resampler.as_mut() {
            if r.run(&decoded, &mut resampled).is_ok() && resampled.samples() > 0 {
                let n = resampled.samples();
                let data = resampled.data(0);
                let f32_slice: &[f32] = unsafe {
                    std::slice::from_raw_parts(data.as_ptr() as *const f32, n)
                };
                samples.extend_from_slice(f32_slice);
            }
        }
    }

    // 리샘플러 플러시
    if let Some(r) = resampler.as_mut() {
        if r.flush(&mut resampled).is_ok() && resampled.samples() > 0 {
            let n = resampled.samples();
            let data = resampled.data(0);
            let f32_slice: &[f32] = unsafe {
                std::slice::from_raw_parts(data.as_ptr() as *const f32, n)
            };
            samples.extend_from_slice(f32_slice);
        }
    }

    eprintln!(
        "[WHISPER] 오디오 추출 결과: {} 샘플 ({:.1}초)",
        samples.len(),
        samples.len() as f32 / 16000.0
    );

    if samples.is_empty() {
        return Err("오디오 샘플 없음 — Opus 파일이 지원되지 않거나 오디오 스트림이 없음".to_string());
    }

    // FFmpeg 로그 레벨 복구
    ffmpeg::util::log::set_level(ffmpeg::util::log::Level::Warning);

    // NaN/Inf 샘플 제거 — Whisper GGML 백엔드가 NaN 검사 assertion 실패 방지
    let nan_count = samples.iter().filter(|s| !s.is_finite()).count();
    if nan_count > 0 {
        eprintln!("[WHISPER] NaN/Inf 샘플 {} 개 발견 → 0.0으로 대체", nan_count);
        for s in samples.iter_mut() {
            if !s.is_finite() {
                *s = 0.0;
            }
        }
    }

    Ok(samples)
}

/// 트랜스크립션 시작 — 백그라운드 스레드에서 실행
/// language: "ko", "en", "ja", ... 또는 "" (자동 감지)
pub fn start_transcription(
    audio_path: String,
    model_path: String,
    language: String,
) -> Arc<TranscriberJob> {
    use whisper_rs::{WhisperContext, WhisperContextParameters, FullParams, SamplingStrategy};

    let progress = Arc::new(AtomicU32::new(0));
    let finished = Arc::new(AtomicBool::new(false));
    let error: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    let segments: Arc<Mutex<Vec<TranscriptSegment>>> = Arc::new(Mutex::new(Vec::new()));
    let abort_requested = Arc::new(AtomicBool::new(false));

    let job = Arc::new(TranscriberJob {
        progress: progress.clone(),
        finished: finished.clone(),
        error: error.clone(),
        segments: segments.clone(),
        abort_requested: abort_requested.clone(),
    });

    let p = progress.clone();
    let f = finished.clone();
    let e = error.clone();
    let s = segments.clone();
    let abort = abort_requested.clone();

    std::thread::spawn(move || {
        eprintln!("[WHISPER] 트랜스크립션 시작: {}", audio_path);

        // 1단계: 오디오 추출 (5%)
        p.store(5, Ordering::SeqCst);
        let audio_samples = match extract_audio_16k_mono(&audio_path) {
            Ok(samples) => samples,
            Err(err) => {
                if let Ok(mut e_lock) = e.lock() {
                    *e_lock = Some(format!("오디오 추출 실패: {}", err));
                }
                f.store(true, Ordering::SeqCst);
                return;
            }
        };

        eprintln!(
            "[WHISPER] 오디오 추출 완료: {} 샘플 ({:.1}초)",
            audio_samples.len(),
            audio_samples.len() as f32 / 16000.0
        );

        // 2단계: Whisper 모델 로드 (10%)
        p.store(10, Ordering::SeqCst);
        let ctx = match WhisperContext::new_with_params(
            &model_path,
            WhisperContextParameters::default(),
        ) {
            Ok(ctx) => ctx,
            Err(err) => {
                if let Ok(mut e_lock) = e.lock() {
                    *e_lock = Some(format!("Whisper 모델 로드 실패: {:?}", err));
                }
                f.store(true, Ordering::SeqCst);
                return;
            }
        };

        p.store(20, Ordering::SeqCst);

        // 3단계: Whisper 파라미터 설정
        let mut params = FullParams::new(SamplingStrategy::Greedy { best_of: 1 });

        // 언어 설정 (빈 문자열 = 자동 감지)
        if language.is_empty() || language == "auto" {
            params.set_language(None);
        } else {
            params.set_language(Some(language.as_str()));
        }

        params.set_print_timestamps(false);
        params.set_print_special(false);
        params.set_suppress_blank(true);
        params.set_suppress_nst(true);

        // 스레드 수 제한: 전체 코어 사용 시 렌더 스레드와 경합 → hang 유발 가능
        let cpu_threads = std::thread::available_parallelism()
            .map(|n| (n.get() / 2).max(1).min(4))
            .unwrap_or(2) as i32;
        params.set_n_threads(cpu_threads);
        eprintln!("[WHISPER] 스레드 수: {}", cpu_threads);

        // 취소 콜백: 비활성화 — set_abort_callback_safe가 GGML 내부 abort() 유발 가능
        let _abort = abort; // unused 경고 방지

        // 오디오 샘플 진단 로그 (failed to encode 디버깅용)
        {
            let min = audio_samples.iter().cloned().fold(f32::INFINITY, f32::min);
            let max = audio_samples.iter().cloned().fold(f32::NEG_INFINITY, f32::max);
            eprintln!("[WHISPER] 샘플 수: {}, 범위: [{:.4}, {:.4}]", audio_samples.len(), min, max);
        }

        // 진행률 콜백: whisper 0~100 → 전체 20~95 범위로 매핑
        let p_cb = p.clone();
        params.set_progress_callback_safe(move |prog| {
            let mapped = 20u32 + (prog as u32 * 75 / 100);
            p_cb.store(mapped, Ordering::SeqCst);
        });

        // 4단계: Whisper 상태 생성
        let mut state = match ctx.create_state() {
            Ok(state) => state,
            Err(err) => {
                if let Ok(mut e_lock) = e.lock() {
                    *e_lock = Some(format!("Whisper 상태 생성 실패: {:?}", err));
                }
                f.store(true, Ordering::SeqCst);
                return;
            }
        };

        // 5단계: 트랜스크립션 실행
        if let Err(err) = state.full(params, &audio_samples) {
            if let Ok(mut e_lock) = e.lock() {
                *e_lock = Some(format!("Whisper 실행 실패: {:?}", err));
            }
            f.store(true, Ordering::SeqCst);
            return;
        }

        // 6단계: 세그먼트 수집
        let num_segments = state.full_n_segments().unwrap_or(0);
        eprintln!("[WHISPER] 세그먼트 수: {}", num_segments);

        let mut result_segments = Vec::new();
        let mut filtered_count = 0usize;
        for i in 0..num_segments {
            let t0 = state.full_get_segment_t0(i).unwrap_or(0);
            let t1 = state.full_get_segment_t1(i).unwrap_or(0);
            let text = state.full_get_segment_text(i).unwrap_or_default();

            // whisper 타임스탬프: centiseconds(1/100s) → ms
            let start_ms = t0 * 10;
            let end_ms = t1 * 10;
            let text = text.trim().to_string();
            eprintln!("[WHISPER] 세그먼트 {}: {:?} ({}~{}ms)", i, text, start_ms, end_ms);

            if text.is_empty() {
                filtered_count += 1;
                continue;
            }

            // hallucination 필터: 확실한 노이즈 토큰만 제거 (필터 기준 완화)
            // "[BLANK_AUDIO]", "(Music)", "(음악)" 등 — 완전히 괄호로 감싼 토큰만
            let is_fully_bracketed = (text.starts_with('(') && text.ends_with(')'))
                || (text.starts_with('[') && text.ends_with(']'));
            if is_fully_bracketed {
                eprintln!("[WHISPER] hallucination 필터 (괄호): {:?}", text);
                filtered_count += 1;
                continue;
            }

            result_segments.push(TranscriptSegment { start_ms, end_ms, text });
        }

        eprintln!(
            "[WHISPER] 필터 결과: 원본 {} → 유효 {} (제거 {})",
            num_segments,
            result_segments.len(),
            filtered_count
        );

        let final_count = result_segments.len();

        if let Ok(mut segs) = s.lock() {
            *segs = result_segments;
        }

        // 세그먼트 0개인 경우: 에러 필드에 진단 메시지 (오류가 아닌 경고)
        // C#에서 이를 별도 처리하여 힌트로 표시
        if final_count == 0 && num_segments > 0 {
            if let Ok(mut e_lock) = e.lock() {
                *e_lock = Some(format!(
                    "WARN:원본 세그먼트 {}개 모두 필터링됨 — 음성은 감지됐으나 텍스트가 제거되었습니다",
                    num_segments
                ));
            }
        } else if final_count == 0 {
            if let Ok(mut e_lock) = e.lock() {
                *e_lock = Some("WARN:Whisper가 음성을 감지하지 못했습니다 (세그먼트 0개)".to_string());
            }
        }

        p.store(100, Ordering::SeqCst);
        eprintln!("[WHISPER] 트랜스크립션 완료: {} 세그먼트", final_count);
        f.store(true, Ordering::SeqCst);
    });

    job
}

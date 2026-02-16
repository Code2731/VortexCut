// Renderer FFI - C# 연동

use crate::rendering::{Renderer, PlaybackEngine};
use crate::timeline::Timeline;
use crate::ffmpeg::Decoder;
use crate::ffi::types::ErrorCode;
use std::ffi::{c_void, c_char, CStr};
use std::sync::{Arc, Mutex};
use std::path::PathBuf;

/// Renderer 생성 (Mutex로 감싸서 thread-safe 보장)
#[no_mangle]
pub extern "C" fn renderer_create(timeline: *mut c_void, out_renderer: *mut *mut c_void) -> i32 {
    if timeline.is_null() || out_renderer.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        // Arc::into_raw()는 *const Mutex<Timeline>을 반환함
        let timeline_arc = Arc::from_raw(timeline as *const Mutex<Timeline>);
        let timeline_clone = Arc::clone(&timeline_arc);

        // 원본 Arc의 소유권 유지 (C#이 관리)
        let _ = Arc::into_raw(timeline_arc);

        let renderer = Renderer::new(timeline_clone);
        // CRITICAL: Renderer를 Mutex로 감싸서 동시 접근 방지
        let renderer_mutex = Box::new(Mutex::new(renderer));
        *out_renderer = Box::into_raw(renderer_mutex) as *mut c_void;

        // 생성 완료
    }

    ErrorCode::Success as i32
}

/// Renderer 파괴
#[no_mangle]
pub extern "C" fn renderer_destroy(renderer: *mut c_void) -> i32 {
    if renderer.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        // Mutex<Renderer>를 Box로 다시 감싸서 drop
        let _ = Box::from_raw(renderer as *mut Mutex<Renderer>);
        // 파괴 완료
    }

    ErrorCode::Success as i32
}

/// 프레임 렌더링 (Mutex로 동시 접근 방지)
#[no_mangle]
pub extern "C" fn renderer_render_frame(
    renderer: *mut c_void,
    timestamp_ms: i64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_data: *mut *mut u8,
    out_data_size: *mut usize,
) -> i32 {
    if renderer.is_null() || out_width.is_null() || out_height.is_null()
        || out_data.is_null() || out_data_size.is_null() {
        // NULL 포인터
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer_mutex = &*(renderer as *const Mutex<Renderer>);

        // [수정] try_lock() 대신 lock()을 사용하여 락 획득 대기 (Mutex 기아 현상 해결)
        // C# ThreadPool에서 호출되므로 대기해도 UI 프리징 없음
        let mut renderer_ref = match renderer_mutex.lock() {
            Ok(guard) => guard,
            Err(poisoned) => {
                eprintln!("renderer_render_frame: Mutex poisoned, recovering");
                poisoned.into_inner()
            }
        };

        match renderer_ref.render_frame(timestamp_ms) {
            Ok(frame) => {
                *out_width = frame.width;
                *out_height = frame.height;
                *out_data_size = frame.data.len();

                let data_box = frame.data.into_boxed_slice();
                *out_data = Box::into_raw(data_box) as *mut u8;

                ErrorCode::Success as i32
            }
            Err(e) => {
                // 에러를 프레임 스킵으로 처리 (C# Exception 방지)
                // render_frame Err는 Timeline lock poison 등 심각한 상황이지만,
                // C#에서 Exception throw → 재생 영구 정지보다는
                // 프레임 스킵(null) 반환이 더 안전
                eprintln!("renderer_render_frame error at {}ms: {}", timestamp_ms, e);
                *out_width = 0;
                *out_height = 0;
                *out_data = std::ptr::null_mut();
                *out_data_size = 0;
                ErrorCode::Success as i32
            }
        }
        // Mutex lock은 여기서 자동으로 해제됨 (MutexGuard drop)
    }
}

/// 재생 모드 설정 (C# 재생 시작/정지 시 호출)
/// playback=1: 재생 모드 (forward_threshold=5000ms, seek 대신 forward decode)
/// playback=0: 스크럽 모드 (forward_threshold=100ms, 즉시 seek)
/// CRITICAL: lock()으로 반드시 실행 보장
/// try_lock() 사용 시 스크럽 렌더 진행 중에 호출되면 무시되어
/// decoder_cache/frame_cache/last_rendered_frame flush 누락 → 이전 영상 표시 버그
#[no_mangle]
pub extern "C" fn renderer_set_playback_mode(renderer: *mut c_void, playback: i32) -> i32 {
    if renderer.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer_mutex = &*(renderer as *const Mutex<Renderer>);
        match renderer_mutex.lock() {
            Ok(mut r) => {
                r.set_playback_mode(playback != 0);
                ErrorCode::Success as i32
            }
            Err(poisoned) => {
                eprintln!("renderer_set_playback_mode: Mutex poisoned, recovering");
                let mut r = poisoned.into_inner();
                r.set_playback_mode(playback != 0);
                ErrorCode::Success as i32
            }
        }
    }
}

/// 프레임 캐시 클리어 (클립 편집 시 C#에서 호출)
/// CRITICAL: lock()으로 반드시 실행 보장 (try_lock → 캐시 클리어 무시 → 이전 프레임 표시)
#[no_mangle]
pub extern "C" fn renderer_clear_cache(renderer: *mut c_void) -> i32 {
    if renderer.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer_mutex = &*(renderer as *const Mutex<Renderer>);
        match renderer_mutex.lock() {
            Ok(mut r) => {
                r.clear_cache();
                ErrorCode::Success as i32
            }
            Err(poisoned) => {
                eprintln!("renderer_clear_cache: Mutex poisoned, recovering");
                let mut r = poisoned.into_inner();
                r.clear_cache();
                ErrorCode::Success as i32
            }
        }
    }
}

/// 캐시 통계 조회 (디버깅/모니터링)
#[no_mangle]
pub extern "C" fn renderer_get_cache_stats(
    renderer: *mut c_void,
    out_cached_frames: *mut u32,
    out_cache_bytes: *mut usize,
) -> i32 {
    if renderer.is_null() || out_cached_frames.is_null() || out_cache_bytes.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer_mutex = &*(renderer as *const Mutex<Renderer>);
        match renderer_mutex.try_lock() {
            Ok(r) => {
                let (frames, bytes) = r.cache_stats();
                *out_cached_frames = frames;
                *out_cache_bytes = bytes;
                ErrorCode::Success as i32
            }
            Err(_) => {
                *out_cached_frames = 0;
                *out_cache_bytes = 0;
                ErrorCode::Success as i32
            }
        }
    }
}

/// 클립 이펙트 설정 (C# Inspector Color 탭 Slider에서 호출)
/// brightness, contrast, saturation, temperature: -1.0 ~ 1.0 (0=원본)
#[no_mangle]
pub extern "C" fn renderer_set_clip_effects(
    renderer: *mut c_void,
    clip_id: u64,
    brightness: f32,
    contrast: f32,
    saturation: f32,
    temperature: f32,
) -> i32 {
    if renderer.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer_mutex = &*(renderer as *const Mutex<Renderer>);
        match renderer_mutex.try_lock() {
            Ok(mut r) => {
                use crate::rendering::effects::EffectParams;
                r.set_clip_effects(clip_id, EffectParams {
                    brightness,
                    contrast,
                    saturation,
                    temperature,
                });
                ErrorCode::Success as i32
            }
            Err(_) => ErrorCode::Success as i32, // busy면 무시 (다음 프레임에서 적용)
        }
    }
}

/// 렌더링된 프레임 데이터 해제
#[no_mangle]
pub extern "C" fn renderer_free_frame_data(data: *mut u8, size: usize) -> i32 {
    if data.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let slice = std::slice::from_raw_parts_mut(data, size);
        let _ = Box::from_raw(slice as *mut [u8]);
    }

    ErrorCode::Success as i32
}

/// 비디오 파일 정보 조회 (duration, width, height, fps)
#[no_mangle]
pub extern "C" fn get_video_info(
    file_path: *const c_char,
    out_duration_ms: *mut i64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_fps: *mut f64,
) -> i32 {
    if file_path.is_null() || out_duration_ms.is_null()
        || out_width.is_null() || out_height.is_null() || out_fps.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let c_str = CStr::from_ptr(file_path);
        let file_path_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return ErrorCode::InvalidParam as i32,
        };

        let path = PathBuf::from(file_path_str);

        let decoder = match Decoder::open(&path) {
            Ok(d) => d,
            Err(e) => {
                eprintln!("get_video_info: Failed to open: {}", e);
                return ErrorCode::Ffmpeg as i32;
            }
        };

        *out_duration_ms = decoder.duration_ms();
        *out_width = decoder.width();
        *out_height = decoder.height();
        *out_fps = decoder.fps();
    }

    ErrorCode::Success as i32
}

/// 비디오 썸네일 생성 (스탠드얼론 함수 - 레거시, 단일 프레임용)
/// NOTE: 다수 썸네일 생성 시 thumbnail_session_* API 사용 권장
#[no_mangle]
pub extern "C" fn generate_video_thumbnail(
    file_path: *const c_char,
    timestamp_ms: i64,
    thumb_width: u32,
    thumb_height: u32,
    out_width: *mut u32,
    out_height: *mut u32,
    out_data: *mut *mut u8,
    out_data_size: *mut usize,
) -> i32 {
    if file_path.is_null() || out_width.is_null() || out_height.is_null()
        || out_data.is_null() || out_data_size.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let c_str = CStr::from_ptr(file_path);
        let file_path_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return ErrorCode::InvalidParam as i32,
        };

        let path = PathBuf::from(file_path_str);

        // 임시 Decoder 생성 (단일 프레임이므로 960x540 기본 해상도)
        let mut decoder = match Decoder::open(&path) {
            Ok(d) => d,
            Err(e) => {
                eprintln!("generate_video_thumbnail: Failed to open: {}", e);
                return ErrorCode::Ffmpeg as i32;
            }
        };

        match decoder.generate_thumbnail(timestamp_ms, thumb_width, thumb_height) {
            Ok(frame) => {
                *out_width = frame.width;
                *out_height = frame.height;
                *out_data_size = frame.data.len();

                let data_box = frame.data.into_boxed_slice();
                *out_data = Box::into_raw(data_box) as *mut u8;

                ErrorCode::Success as i32
            }
            Err(e) => {
                eprintln!("generate_video_thumbnail: Failed at {}ms: {}", timestamp_ms, e);
                ErrorCode::Ffmpeg as i32
            }
        }
    }
}

// ============================================================
// PlaybackEngine FFI (재생 전용 백그라운드 프리페치)
// ============================================================

/// PlaybackEngine 생성
#[no_mangle]
pub extern "C" fn playback_engine_create(
    timeline: *mut c_void,
    out_engine: *mut *mut c_void,
) -> i32 {
    if timeline.is_null() || out_engine.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let timeline_arc = Arc::from_raw(timeline as *const Mutex<Timeline>);
        let timeline_clone = Arc::clone(&timeline_arc);
        let _ = Arc::into_raw(timeline_arc); // 원본 소유권 유지

        let engine = PlaybackEngine::new(timeline_clone);
        let engine_mutex = Box::new(Mutex::new(engine));
        *out_engine = Box::into_raw(engine_mutex) as *mut c_void;
    }

    ErrorCode::Success as i32
}

/// PlaybackEngine 시작 (백그라운드 프리페치 시작)
/// CRITICAL: lock()으로 반드시 실행 보장 (try_lock → start 무시 = 이전 영상 재생 버그)
#[no_mangle]
pub extern "C" fn playback_engine_start(
    engine: *mut c_void,
    start_ms: i64,
) -> i32 {
    if engine.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let engine_mutex = &*(engine as *const Mutex<PlaybackEngine>);
        match engine_mutex.lock() {
            Ok(mut e) => {
                e.start(start_ms);
                ErrorCode::Success as i32
            }
            Err(poisoned) => {
                // Mutex poisoned (이전 스레드 panic) → 복구 시도
                eprintln!("playback_engine_start: Mutex poisoned, recovering");
                let mut e = poisoned.into_inner();
                e.start(start_ms);
                ErrorCode::Success as i32
            }
        }
    }
}

/// PlaybackEngine 정지 (백그라운드 스레드 종료)
/// CRITICAL: lock()으로 반드시 실행 보장 (try_lock → stop 무시 = 이전 fill_loop 계속 실행)
#[no_mangle]
pub extern "C" fn playback_engine_stop(engine: *mut c_void) -> i32 {
    if engine.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let engine_mutex = &*(engine as *const Mutex<PlaybackEngine>);
        match engine_mutex.lock() {
            Ok(mut e) => {
                e.stop();
                ErrorCode::Success as i32
            }
            Err(poisoned) => {
                eprintln!("playback_engine_stop: Mutex poisoned, recovering");
                let mut e = poisoned.into_inner();
                e.stop();
                ErrorCode::Success as i32
            }
        }
    }
}

/// timestamp에 가장 가까운 프레임 조회 (디코딩 없음, 즉시 반환)
/// out_actual_timestamp_ms: 큐에서 실제 매칭된 프레임의 timestamp (진단용)
#[no_mangle]
pub extern "C" fn playback_engine_try_get_frame(
    engine: *mut c_void,
    timestamp_ms: i64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_data: *mut *mut u8,
    out_data_size: *mut usize,
    out_actual_timestamp_ms: *mut i64,
) -> i32 {
    if engine.is_null() || out_width.is_null() || out_height.is_null()
        || out_data.is_null() || out_data_size.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let engine_mutex = &*(engine as *const Mutex<PlaybackEngine>);

        // try_lock으로 Mutex 경합 시 즉시 실패 (프레임 스킵)
        let engine_ref = match engine_mutex.try_lock() {
            Ok(e) => e,
            Err(_) => {
                *out_width = 0;
                *out_height = 0;
                *out_data = std::ptr::null_mut();
                *out_data_size = 1; // 1 = engine mutex busy
                if !out_actual_timestamp_ms.is_null() { *out_actual_timestamp_ms = -1; }
                return ErrorCode::Success as i32;
            }
        };

        match engine_ref.try_get_frame(timestamp_ms) {
            Some(frame) => {
                *out_width = frame.width;
                *out_height = frame.height;
                *out_data_size = frame.data.len();
                if !out_actual_timestamp_ms.is_null() {
                    *out_actual_timestamp_ms = frame.timestamp_ms;
                }

                let data_box = frame.data.into_boxed_slice();
                *out_data = Box::into_raw(data_box) as *mut u8;

                ErrorCode::Success as i32
            }
            None => {
                *out_width = 0;
                *out_height = 0;
                *out_data = std::ptr::null_mut();
                *out_data_size = 2; // 2 = queue empty or no match
                if !out_actual_timestamp_ms.is_null() { *out_actual_timestamp_ms = -1; }
                ErrorCode::Success as i32
            }
        }
    }
}

/// PlaybackEngine 파괴
#[no_mangle]
pub extern "C" fn playback_engine_destroy(engine: *mut c_void) -> i32 {
    if engine.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let _ = Box::from_raw(engine as *mut Mutex<PlaybackEngine>);
    }

    ErrorCode::Success as i32
}

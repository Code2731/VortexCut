// Renderer FFI - C# 연동

use crate::rendering::Renderer;
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

        println!("✅ renderer_create: Renderer created with Mutex protection");
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
        println!("✅ renderer_destroy: Renderer destroyed");
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
        println!("❌ renderer_render_frame: NULL pointer detected!");
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer_mutex = &*(renderer as *const Mutex<Renderer>);

        let mut renderer_ref = match renderer_mutex.try_lock() {
            Ok(r) => r,
            Err(_) => {
                // Mutex busy → 프레임 스킵 (출력 파라미터 초기화)
                *out_width = 0;
                *out_height = 0;
                *out_data = std::ptr::null_mut();
                *out_data_size = 0;
                return ErrorCode::Success as i32;
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
                eprintln!("❌ renderer_render_frame failed at {}ms: {}", timestamp_ms, e);
                ErrorCode::RenderFailed as i32
            }
        }
        // Mutex lock은 여기서 자동으로 해제됨 (MutexGuard drop)
    }
}

/// 프레임 캐시 클리어 (클립 편집 시 C#에서 호출)
#[no_mangle]
pub extern "C" fn renderer_clear_cache(renderer: *mut c_void) -> i32 {
    if renderer.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer_mutex = &*(renderer as *const Mutex<Renderer>);
        match renderer_mutex.try_lock() {
            Ok(mut r) => {
                r.clear_cache();
                ErrorCode::Success as i32
            }
            Err(_) => ErrorCode::Success as i32, // busy면 무시
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
            Err(e) => {
                println!("❌ get_video_info: Invalid UTF-8: {}", e);
                return ErrorCode::InvalidParam as i32;
            }
        };

        let path = PathBuf::from(file_path_str);
        println!("📋 get_video_info: file={}", file_path_str);

        let decoder = match Decoder::open(&path) {
            Ok(d) => d,
            Err(e) => {
                println!("❌ get_video_info: Failed to open decoder: {}", e);
                return ErrorCode::Ffmpeg as i32;
            }
        };

        *out_duration_ms = decoder.duration_ms();
        *out_width = decoder.width();
        *out_height = decoder.height();
        *out_fps = decoder.fps();

        println!("✅ get_video_info: duration={}ms, {}x{}, fps={:.2}",
                 decoder.duration_ms(), decoder.width(), decoder.height(), decoder.fps());
    }

    ErrorCode::Success as i32
}

/// 비디오 썸네일 생성 (스탠드얼론 함수)
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
        println!("❌ generate_video_thumbnail: NULL pointer detected!");
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        // C 문자열을 Rust 문자열로 변환
        let c_str = CStr::from_ptr(file_path);
        let file_path_str = match c_str.to_str() {
            Ok(s) => s,
            Err(e) => {
                println!("❌ generate_video_thumbnail: Invalid UTF-8: {}", e);
                return ErrorCode::InvalidParam as i32;
            }
        };

        let path = PathBuf::from(file_path_str);
        println!("📸 generate_video_thumbnail: file={}, timestamp={}ms, size={}x{}",
                 file_path_str, timestamp_ms, thumb_width, thumb_height);

        // 임시 Decoder 생성
        let mut decoder = match Decoder::open(&path) {
            Ok(d) => d,
            Err(e) => {
                println!("❌ generate_video_thumbnail: Failed to open decoder: {}", e);
                return ErrorCode::Ffmpeg as i32;
            }
        };

        // 썸네일 생성
        match decoder.generate_thumbnail(timestamp_ms, thumb_width, thumb_height) {
            Ok(frame) => {
                println!("✅ generate_video_thumbnail: Thumbnail generated {}x{}, {} bytes",
                         frame.width, frame.height, frame.data.len());

                *out_width = frame.width;
                *out_height = frame.height;
                *out_data_size = frame.data.len();

                // 데이터를 힙에 할당하고 포인터 반환
                let data_box = frame.data.into_boxed_slice();
                *out_data = Box::into_raw(data_box) as *mut u8;

                ErrorCode::Success as i32
            }
            Err(e) => {
                println!("❌ generate_video_thumbnail: Failed to generate thumbnail: {}", e);
                ErrorCode::Ffmpeg as i32
            }
        }
        // Decoder는 여기서 자동으로 drop됨
    }
}

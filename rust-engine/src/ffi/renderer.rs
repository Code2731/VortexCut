// Renderer FFI - C# 연동

use crate::rendering::Renderer;
use crate::timeline::Timeline;
use crate::ffi::types::ErrorCode;
use std::ffi::c_void;
use std::sync::{Arc, Mutex};

/// Renderer 생성
#[no_mangle]
pub extern "C" fn renderer_create(timeline: *mut c_void, out_renderer: *mut *mut c_void) -> i32 {
    if timeline.is_null() || out_renderer.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let timeline_arc = Arc::from_raw(timeline as *const Mutex<Timeline>);
        let timeline_clone = Arc::clone(&timeline_arc);

        // Arc를 다시 raw로 변환 (소유권 유지)
        let _ = Arc::into_raw(timeline_arc);

        let renderer = Box::new(Renderer::new(timeline_clone));
        *out_renderer = Box::into_raw(renderer) as *mut c_void;
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
        let _ = Box::from_raw(renderer as *mut Renderer);
    }

    ErrorCode::Success as i32
}

/// 프레임 렌더링
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
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let renderer = &mut *(renderer as *mut Renderer);

        match renderer.render_frame(timestamp_ms) {
            Ok(frame) => {
                *out_width = frame.width;
                *out_height = frame.height;
                *out_data_size = frame.data.len();

                // 데이터를 힙에 할당하고 포인터 반환
                let data_box = frame.data.into_boxed_slice();
                *out_data = Box::into_raw(data_box) as *mut u8;

                ErrorCode::Success as i32
            }
            Err(_) => ErrorCode::RenderFailed as i32,
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

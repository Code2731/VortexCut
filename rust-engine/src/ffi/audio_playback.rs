// 오디오 재생 FFI - C# P/Invoke 연동
// AudioPlayback 생성/정지/일시정지/재개/파괴

use crate::audio::playback::AudioPlayback;
use crate::ffi::types::ErrorCode;
use crate::timeline::Timeline;
use std::ffi::c_void;
use std::sync::{Arc, Mutex};

/// 오디오 재생 시작
/// timeline: Arc<Mutex<Timeline>>의 raw pointer (소유권 변경 없음)
/// start_time_ms: 재생 시작 위치
/// out_handle: AudioPlayback 핸들 반환
#[no_mangle]
pub extern "C" fn audio_playback_start(
    timeline: *mut c_void,
    start_time_ms: i64,
    out_handle: *mut *mut c_void,
) -> i32 {
    if timeline.is_null() || out_handle.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        // Timeline Arc 복제 (원본 소유권 유지)
        let timeline_arc = Arc::from_raw(timeline as *const Mutex<Timeline>);
        let timeline_clone = Arc::clone(&timeline_arc);
        let _ = Arc::into_raw(timeline_arc); // 원본 유지

        match AudioPlayback::start(timeline_clone, start_time_ms) {
            Ok(playback) => {
                let boxed = Box::new(playback);
                *out_handle = Box::into_raw(boxed) as *mut c_void;
                ErrorCode::Success as i32
            }
            Err(e) => {
                eprintln!("[AUDIO_FFI] 재생 시작 실패: {}", e);
                *out_handle = std::ptr::null_mut();
                ErrorCode::Unknown as i32
            }
        }
    }
}

/// 오디오 재생 정지
#[no_mangle]
pub extern "C" fn audio_playback_stop(handle: *mut c_void) -> i32 {
    if handle.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let playback = &mut *(handle as *mut AudioPlayback);
        playback.stop();
    }

    ErrorCode::Success as i32
}

/// 오디오 일시정지
#[no_mangle]
pub extern "C" fn audio_playback_pause(handle: *mut c_void) -> i32 {
    if handle.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let playback = &*(handle as *mut AudioPlayback);
        playback.pause();
    }

    ErrorCode::Success as i32
}

/// 오디오 재개
#[no_mangle]
pub extern "C" fn audio_playback_resume(handle: *mut c_void) -> i32 {
    if handle.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let playback = &*(handle as *mut AudioPlayback);
        playback.resume();
    }

    ErrorCode::Success as i32
}

/// 오디오 재생 객체 파괴 (메모리 해제)
#[no_mangle]
pub extern "C" fn audio_playback_destroy(handle: *mut c_void) -> i32 {
    if handle.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        // Box로 되돌려서 Drop 호출 → stop() + 자원 해제
        let _ = Box::from_raw(handle as *mut AudioPlayback);
    }

    ErrorCode::Success as i32
}

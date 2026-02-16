// Timeline FFI 함수
// C#에서 Timeline을 생성/관리하기 위한 FFI 인터페이스

use std::ffi::CStr;
use std::os::raw::c_char;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use crate::timeline::{Timeline, TransitionType};
use super::types::{ERROR_SUCCESS, ERROR_NULL_PTR, ERROR_INVALID_PARAM};

type TimelineArc = Arc<Mutex<Timeline>>;

/// Timeline 생성 (Arc<Mutex> 래핑)
#[no_mangle]
pub extern "C" fn timeline_create(
    width: u32,
    height: u32,
    fps: f64,
    out_timeline: *mut *mut std::ffi::c_void,
) -> i32 {
    if out_timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    if width == 0 || height == 0 || fps <= 0.0 {
        return ERROR_INVALID_PARAM;
    }

    let timeline = Arc::new(Mutex::new(Timeline::new(width, height, fps)));

    unsafe {
        *out_timeline = Arc::into_raw(timeline) as *mut std::ffi::c_void;
    }

    ERROR_SUCCESS
}

/// Timeline 파괴 (메모리 해제)
#[no_mangle]
pub extern "C" fn timeline_destroy(timeline: *mut std::ffi::c_void) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let _ = Arc::from_raw(timeline as *const Mutex<Timeline>);
    }

    ERROR_SUCCESS
}

/// 비디오 트랙 추가
#[no_mangle]
pub extern "C" fn timeline_add_video_track(
    timeline: *mut std::ffi::c_void,
    out_track_id: *mut u64,
) -> i32 {
    if timeline.is_null() || out_track_id.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };
        let track_id = timeline.add_video_track();
        *out_track_id = track_id;
    }

    ERROR_SUCCESS
}

/// 오디오 트랙 추가
#[no_mangle]
pub extern "C" fn timeline_add_audio_track(
    timeline: *mut std::ffi::c_void,
    out_track_id: *mut u64,
) -> i32 {
    if timeline.is_null() || out_track_id.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };
        let track_id = timeline.add_audio_track();
        *out_track_id = track_id;
    }

    ERROR_SUCCESS
}

/// 비디오 클립 추가
/// file_path: 원본 경로 (Export, 오디오용)
/// proxy_path: 프리뷰용 Proxy 경로 (null이면 원본만 사용)
#[no_mangle]
pub extern "C" fn timeline_add_video_clip(
    timeline: *mut std::ffi::c_void,
    track_id: u64,
    file_path: *const c_char,
    proxy_path: *const c_char,
    start_time_ms: i64,
    duration_ms: i64,
    out_clip_id: *mut u64,
) -> i32 {
    if timeline.is_null() || file_path.is_null() || out_clip_id.is_null() {
        return ERROR_NULL_PTR;
    }

    if duration_ms <= 0 {
        return ERROR_INVALID_PARAM;
    }

    let path_str = unsafe {
        match CStr::from_ptr(file_path).to_str() {
            Ok(s) => s,
            Err(_) => return ERROR_INVALID_PARAM,
        }
    };

    let path = PathBuf::from(path_str);

    let proxy = if proxy_path.is_null() {
        None
    } else {
        unsafe {
            match CStr::from_ptr(proxy_path).to_str() {
                Ok(s) if !s.is_empty() => Some(PathBuf::from(s)),
                _ => None,
            }
        }
    };

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        match timeline.add_video_clip(track_id, path, start_time_ms, duration_ms, proxy) {
            Some(clip_id) => {
                *out_clip_id = clip_id;
                ERROR_SUCCESS
            }
            None => ERROR_INVALID_PARAM, // 트랙을 찾을 수 없음
        }
    }
}

/// 오디오 클립 추가
#[no_mangle]
pub extern "C" fn timeline_add_audio_clip(
    timeline: *mut std::ffi::c_void,
    track_id: u64,
    file_path: *const c_char,
    start_time_ms: i64,
    duration_ms: i64,
    out_clip_id: *mut u64,
) -> i32 {
    if timeline.is_null() || file_path.is_null() || out_clip_id.is_null() {
        return ERROR_NULL_PTR;
    }

    if duration_ms <= 0 {
        return ERROR_INVALID_PARAM;
    }

    let path_str = unsafe {
        match CStr::from_ptr(file_path).to_str() {
            Ok(s) => s,
            Err(_) => return ERROR_INVALID_PARAM,
        }
    };

    let path = PathBuf::from(path_str);

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        match timeline.add_audio_clip(track_id, path, start_time_ms, duration_ms) {
            Some(clip_id) => {
                *out_clip_id = clip_id;
                ERROR_SUCCESS
            }
            None => ERROR_INVALID_PARAM,
        }
    }
}

/// 비디오 클립 제거
#[no_mangle]
pub extern "C" fn timeline_remove_video_clip(
    timeline: *mut std::ffi::c_void,
    track_id: u64,
    clip_id: u64,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        if timeline.remove_video_clip(track_id, clip_id) {
            ERROR_SUCCESS
        } else {
            ERROR_INVALID_PARAM
        }
    }
}

/// 오디오 클립 제거
#[no_mangle]
pub extern "C" fn timeline_remove_audio_clip(
    timeline: *mut std::ffi::c_void,
    track_id: u64,
    clip_id: u64,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        if timeline.remove_audio_clip(track_id, clip_id) {
            ERROR_SUCCESS
        } else {
            ERROR_INVALID_PARAM
        }
    }
}

/// 타임라인 총 길이 가져오기 (ms)
#[no_mangle]
pub extern "C" fn timeline_get_duration(
    timeline: *const std::ffi::c_void,
    out_duration_ms: *mut i64,
) -> i32 {
    if timeline.is_null() || out_duration_ms.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        *out_duration_ms = timeline.duration_ms();
    }

    ERROR_SUCCESS
}

/// 비디오 트랙 개수 가져오기
#[no_mangle]
pub extern "C" fn timeline_get_video_track_count(
    timeline: *const std::ffi::c_void,
    out_count: *mut usize,
) -> i32 {
    if timeline.is_null() || out_count.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        *out_count = timeline.video_tracks.len();
    }

    ERROR_SUCCESS
}

/// 오디오 트랙 개수 가져오기
#[no_mangle]
pub extern "C" fn timeline_get_audio_track_count(
    timeline: *const std::ffi::c_void,
    out_count: *mut usize,
) -> i32 {
    if timeline.is_null() || out_count.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        *out_count = timeline.audio_tracks.len();
    }

    ERROR_SUCCESS
}

/// 특정 비디오 트랙의 클립 개수 가져오기
#[no_mangle]
pub extern "C" fn timeline_get_video_clip_count(
    timeline: *const std::ffi::c_void,
    track_id: u64,
    out_count: *mut usize,
) -> i32 {
    if timeline.is_null() || out_count.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        if let Some(track) = timeline.video_tracks.iter().find(|t| t.id == track_id) {
            *out_count = track.clips.len();
            ERROR_SUCCESS
        } else {
            ERROR_INVALID_PARAM
        }
    }
}

/// 비디오 클립의 trim_start_ms 설정 (Razor 분할용)
#[no_mangle]
pub extern "C" fn timeline_set_video_clip_trim(
    timeline: *mut std::ffi::c_void,
    track_id: u64,
    clip_id: u64,
    trim_start_ms: i64,
    trim_end_ms: i64,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        if let Some(track) = timeline.video_tracks.iter_mut().find(|t| t.id == track_id) {
            if let Some(clip) = track.get_clip_by_id_mut(clip_id) {
                clip.trim_start_ms = trim_start_ms;
                clip.trim_end_ms = trim_end_ms;
                return ERROR_SUCCESS;
            }
        }
    }

    ERROR_INVALID_PARAM
}

/// 클립 볼륨 설정 (비디오/오디오 트랙 모두 순회)
/// volume: 0.0~2.0
#[no_mangle]
pub extern "C" fn timeline_set_clip_volume(
    timeline: *mut std::ffi::c_void,
    clip_id: u64,
    volume: f32,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        // 비디오 트랙에서 찾기
        for track in &mut timeline.video_tracks {
            if let Some(clip) = track.get_clip_by_id_mut(clip_id) {
                clip.volume = volume;
                return ERROR_SUCCESS;
            }
        }

        // 오디오 트랙에서 찾기
        for track in &mut timeline.audio_tracks {
            if let Some(clip) = track.clips.iter_mut().find(|c| c.id == clip_id) {
                clip.volume = volume;
                return ERROR_SUCCESS;
            }
        }
    }

    ERROR_INVALID_PARAM
}

/// 클립 속도 설정 (비디오/오디오 트랙 모두 순회)
/// speed: 0.25~4.0
#[no_mangle]
pub extern "C" fn timeline_set_clip_speed(
    timeline: *mut std::ffi::c_void,
    clip_id: u64,
    speed: f64,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        // 비디오 트랙에서 찾기
        for track in &mut timeline.video_tracks {
            if let Some(clip) = track.get_clip_by_id_mut(clip_id) {
                clip.speed = speed;
                return ERROR_SUCCESS;
            }
        }

        // 오디오 트랙에서 찾기
        for track in &mut timeline.audio_tracks {
            if let Some(clip) = track.clips.iter_mut().find(|c| c.id == clip_id) {
                clip.speed = speed;
                return ERROR_SUCCESS;
            }
        }
    }

    ERROR_INVALID_PARAM
}

/// 오디오 클립 페이드 설정
/// fade_in_ms, fade_out_ms: 0 = 페이드 없음
#[no_mangle]
pub extern "C" fn timeline_set_clip_fade(
    timeline: *mut std::ffi::c_void,
    clip_id: u64,
    fade_in_ms: i64,
    fade_out_ms: i64,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        // 오디오 트랙에서 찾기
        for track in &mut timeline.audio_tracks {
            if let Some(clip) = track.clips.iter_mut().find(|c| c.id == clip_id) {
                clip.fade_in_ms = fade_in_ms;
                clip.fade_out_ms = fade_out_ms;
                return ERROR_SUCCESS;
            }
        }
    }

    ERROR_INVALID_PARAM
}

/// 클립 트랜지션 타입 설정 (비디오 트랙 only)
/// transition_type: 0=None, 1=Crossfade, 2=FadeBlack, 3=WipeLeft, 4=WipeRight, 5=WipeUp, 6=WipeDown
#[no_mangle]
pub extern "C" fn timeline_set_clip_transition(
    timeline: *mut std::ffi::c_void,
    clip_id: u64,
    transition_type: u32,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut timeline = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        for track in &mut timeline.video_tracks {
            if let Some(clip) = track.get_clip_by_id_mut(clip_id) {
                clip.transition_type = TransitionType::from_u32(transition_type);
                return ERROR_SUCCESS;
            }
        }
    }

    ERROR_INVALID_PARAM
}

/// 트랙 뮤트 설정 (비디오 + 오디오 트랙 공용)
/// muted: 0=unmute, 1=mute
#[no_mangle]
pub extern "C" fn timeline_set_track_muted(
    timeline: *mut std::ffi::c_void,
    track_id: u64,
    muted: i32,
) -> i32 {
    if timeline.is_null() {
        return ERROR_NULL_PTR;
    }

    unsafe {
        let timeline_arc = &*(timeline as *const Mutex<Timeline>);
        let mut tl = match timeline_arc.lock() {
            Ok(t) => t,
            Err(_) => return ERROR_INVALID_PARAM,
        };

        if tl.set_track_muted(track_id, muted != 0) {
            ERROR_SUCCESS
        } else {
            ERROR_INVALID_PARAM
        }
    }
}

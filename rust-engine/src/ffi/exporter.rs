// Exporter FFI - C# P/Invoke 연동
// Export 작업 생성/진행률/취소/파괴

use crate::encoding::exporter::{ExportConfig, ExportJob};
use crate::ffi::types::ErrorCode;
use crate::subtitle::overlay::{SubtitleOverlay, SubtitleOverlayList};
use crate::timeline::Timeline;
use std::ffi::{c_void, c_char, CStr, CString};
use std::sync::{Arc, Mutex};

/// Export 시작 (백그라운드 스레드에서 실행)
/// timeline: Arc<Mutex<Timeline>>의 raw pointer
/// output_path: UTF-8 인코딩된 출력 파일 경로
/// out_job: ExportJob 핸들 반환
#[no_mangle]
pub extern "C" fn exporter_start(
    timeline: *mut c_void,
    output_path: *const c_char,
    width: u32,
    height: u32,
    fps: f64,
    crf: u32,
    out_job: *mut *mut c_void,
) -> i32 {
    if timeline.is_null() || output_path.is_null() || out_job.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        // output_path → Rust String
        let c_str = CStr::from_ptr(output_path);
        let output_path_str = match c_str.to_str() {
            Ok(s) => s.to_string(),
            Err(_) => return ErrorCode::InvalidParam as i32,
        };

        // Timeline Arc 복제 (원본 소유권 유지)
        let timeline_arc = Arc::from_raw(timeline as *const Mutex<Timeline>);
        let timeline_clone = Arc::clone(&timeline_arc);
        let _ = Arc::into_raw(timeline_arc); // 원본 유지

        let config = ExportConfig {
            output_path: output_path_str,
            width,
            height,
            fps,
            crf,
            encoder_type: 0, // Auto
        };

        // ExportJob 시작 (백그라운드 스레드)
        let job = ExportJob::start(timeline_clone, config);
        let job_box = Box::new(job);
        *out_job = Box::into_raw(job_box) as *mut c_void;
    }

    ErrorCode::Success as i32
}

/// Export 진행률 가져오기 (0~100)
#[no_mangle]
pub extern "C" fn exporter_get_progress(job: *mut c_void) -> u32 {
    if job.is_null() {
        return 0;
    }

    unsafe {
        let job_ref = &*(job as *const ExportJob);
        job_ref.get_progress()
    }
}

/// Export 완료 여부 확인
/// 반환: 1=완료, 0=진행중
#[no_mangle]
pub extern "C" fn exporter_is_finished(job: *mut c_void) -> i32 {
    if job.is_null() {
        return 1; // null이면 완료로 처리
    }

    unsafe {
        let job_ref = &*(job as *const ExportJob);
        if job_ref.is_finished() { 1 } else { 0 }
    }
}

/// Export 에러 메시지 가져오기
/// out_error: 에러 문자열 포인터 (없으면 null)
/// 반환 후 string_free()로 해제 필요
#[no_mangle]
pub extern "C" fn exporter_get_error(
    job: *mut c_void,
    out_error: *mut *mut c_char,
) -> i32 {
    if job.is_null() || out_error.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let job_ref = &*(job as *const ExportJob);

        match job_ref.get_error() {
            Some(msg) => {
                match CString::new(msg) {
                    Ok(c_str) => {
                        *out_error = c_str.into_raw();
                    }
                    Err(_) => {
                        *out_error = std::ptr::null_mut();
                    }
                }
            }
            None => {
                *out_error = std::ptr::null_mut();
            }
        }
    }

    ErrorCode::Success as i32
}

/// Export 취소
#[no_mangle]
pub extern "C" fn exporter_cancel(job: *mut c_void) -> i32 {
    if job.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let job_ref = &*(job as *const ExportJob);
        job_ref.cancel();
    }

    ErrorCode::Success as i32
}

/// ExportJob 파괴 (메모리 해제)
/// Export 완료/취소 후 호출
#[no_mangle]
pub extern "C" fn exporter_destroy(job: *mut c_void) -> i32 {
    if job.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let _ = Box::from_raw(job as *mut ExportJob);
    }

    ErrorCode::Success as i32
}

// ==================== 자막 오버레이 FFI ====================

/// 자막 오버레이 목록 생성
/// 반환: SubtitleOverlayList 핸들 (exporter_free_subtitle_list로 해제)
#[no_mangle]
pub extern "C" fn exporter_create_subtitle_list() -> *mut c_void {
    let list = Box::new(SubtitleOverlayList::new());
    Box::into_raw(list) as *mut c_void
}

/// 자막 오버레이 추가
/// rgba_ptr: RGBA 비트맵 데이터 포인터 (width * height * 4 bytes)
/// rgba_len: 바이트 수
#[no_mangle]
pub extern "C" fn exporter_subtitle_list_add(
    list: *mut c_void,
    start_ms: i64,
    end_ms: i64,
    x: i32,
    y: i32,
    width: u32,
    height: u32,
    rgba_ptr: *const u8,
    rgba_len: u32,
) -> i32 {
    if list.is_null() || rgba_ptr.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    let expected_size = (width as usize) * (height as usize) * 4;
    if (rgba_len as usize) < expected_size {
        return ErrorCode::InvalidParam as i32;
    }

    unsafe {
        let list_ref = &mut *(list as *mut SubtitleOverlayList);
        let data = std::slice::from_raw_parts(rgba_ptr, expected_size).to_vec();

        list_ref.overlays.push(SubtitleOverlay {
            start_ms,
            end_ms,
            x,
            y,
            width,
            height,
            rgba_data: data,
        });
    }

    ErrorCode::Success as i32
}

/// 자막 포함 Export 시작 (v2)
/// subtitle_list: exporter_create_subtitle_list()로 생성한 핸들 (null이면 자막 없음)
/// 자막 목록의 소유권이 Rust로 이전됨 — 별도로 free할 필요 없음
#[no_mangle]
pub extern "C" fn exporter_start_v2(
    timeline: *mut c_void,
    output_path: *const c_char,
    width: u32,
    height: u32,
    fps: f64,
    crf: u32,
    subtitle_list: *mut c_void,
    out_job: *mut *mut c_void,
) -> i32 {
    if timeline.is_null() || output_path.is_null() || out_job.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let c_str = CStr::from_ptr(output_path);
        let output_path_str = match c_str.to_str() {
            Ok(s) => s.to_string(),
            Err(_) => return ErrorCode::InvalidParam as i32,
        };

        let timeline_arc = Arc::from_raw(timeline as *const Mutex<Timeline>);
        let timeline_clone = Arc::clone(&timeline_arc);
        let _ = Arc::into_raw(timeline_arc);

        let config = ExportConfig {
            output_path: output_path_str,
            width,
            height,
            fps,
            crf,
            encoder_type: 0, // Auto
        };

        // 자막 목록 소유권 이전 (null이면 None)
        let subtitles = if subtitle_list.is_null() {
            None
        } else {
            Some(*Box::from_raw(subtitle_list as *mut SubtitleOverlayList))
        };

        let job = ExportJob::start_with_subtitles(timeline_clone, config, subtitles);
        let job_box = Box::new(job);
        *out_job = Box::into_raw(job_box) as *mut c_void;
    }

    ErrorCode::Success as i32
}

/// 자막 포함 Export 시작 (v3) — 인코더 타입 선택 지원
/// encoder_type: 0=Auto, 1=Software, 2=NVENC, 3=QSV, 4=AMF
/// subtitle_list: null이면 자막 없음, 소유권 Rust로 이전
#[no_mangle]
pub extern "C" fn exporter_start_v3(
    timeline: *mut c_void,
    output_path: *const c_char,
    width: u32,
    height: u32,
    fps: f64,
    crf: u32,
    encoder_type: u32,
    subtitle_list: *mut c_void,
    out_job: *mut *mut c_void,
) -> i32 {
    if timeline.is_null() || output_path.is_null() || out_job.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let c_str = CStr::from_ptr(output_path);
        let output_path_str = match c_str.to_str() {
            Ok(s) => s.to_string(),
            Err(_) => return ErrorCode::InvalidParam as i32,
        };

        let timeline_arc = Arc::from_raw(timeline as *const Mutex<Timeline>);
        let timeline_clone = Arc::clone(&timeline_arc);
        let _ = Arc::into_raw(timeline_arc);

        let config = ExportConfig {
            output_path: output_path_str,
            width,
            height,
            fps,
            crf,
            encoder_type,
        };

        let subtitles = if subtitle_list.is_null() {
            None
        } else {
            Some(*Box::from_raw(subtitle_list as *mut SubtitleOverlayList))
        };

        let job = ExportJob::start_with_subtitles(timeline_clone, config, subtitles);
        let job_box = Box::new(job);
        *out_job = Box::into_raw(job_box) as *mut c_void;
    }

    ErrorCode::Success as i32
}

/// 사용 가능한 인코더 탐지 (비트마스크 반환)
/// bit 0 = libx264 (1), bit 1 = NVENC (2), bit 2 = QSV (4), bit 3 = AMF (8)
#[no_mangle]
pub extern "C" fn exporter_detect_encoders() -> u32 {
    crate::encoding::encoder::detect_available_encoders()
}

/// 자막 오버레이 목록 해제 (Export에 전달하지 않고 취소할 때만 사용)
#[no_mangle]
pub extern "C" fn exporter_free_subtitle_list(list: *mut c_void) -> i32 {
    if list.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let _ = Box::from_raw(list as *mut SubtitleOverlayList);
    }

    ErrorCode::Success as i32
}

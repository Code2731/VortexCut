// Exporter FFI - C# P/Invoke 연동
// Export 작업 생성/진행률/취소/파괴

use crate::encoding::exporter::{ExportConfig, ExportJob};
use crate::ffi::types::ErrorCode;
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

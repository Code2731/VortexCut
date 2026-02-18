// Transcriber FFI — C# P/Invoke 연동
// Export 패턴과 동일: 폴링 방식 (progress/finished/error)
// TranscriberJob 핸들을 opaque pointer로 노출

use crate::ffi::types::ErrorCode;
use crate::transcription::{start_transcription, TranscriberJob};
use std::ffi::{c_char, CStr, CString};
use std::os::raw::c_void;
use std::sync::Arc;

/// C-compatible 세그먼트 구조체 (C# Marshal용)
#[repr(C)]
pub struct CTranscriptSegment {
    pub start_ms: i64,
    pub end_ms: i64,
    /// UTF-8 문자열 포인터 (null-terminated)
    pub text_ptr: *const c_char,
    pub text_len: u32,
}

/// 트랜스크립션 시작
/// audio_path: 미디어 파일 경로 (UTF-8)
/// model_path: ggml 모델 파일 경로 (UTF-8)
/// language: "ko"/"en"/"ja"/... 또는 "" (자동)
/// out_job: TranscriberJob 핸들 반환
/// 반환: 0=성공, 음수=오류
#[no_mangle]
pub extern "C" fn transcriber_start(
    audio_path: *const c_char,
    model_path: *const c_char,
    language: *const c_char,
    out_job: *mut *mut c_void,
) -> i32 {
    if audio_path.is_null() || model_path.is_null() || out_job.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    unsafe {
        let audio_path_str = match CStr::from_ptr(audio_path).to_str() {
            Ok(s) => s.to_string(),
            Err(_) => return ErrorCode::InvalidParam as i32,
        };

        let model_path_str = match CStr::from_ptr(model_path).to_str() {
            Ok(s) => s.to_string(),
            Err(_) => return ErrorCode::InvalidParam as i32,
        };

        let language_str = if language.is_null() {
            String::new()
        } else {
            CStr::from_ptr(language)
                .to_str()
                .unwrap_or("")
                .to_string()
        };

        let job = start_transcription(audio_path_str, model_path_str, language_str);

        // Arc<TranscriberJob> → raw pointer (Arc into_raw)
        let raw = Arc::into_raw(job) as *mut c_void;
        *out_job = raw;
    }

    ErrorCode::Success as i32
}

/// 진행률 가져오기 (0~100)
#[no_mangle]
pub extern "C" fn transcriber_get_progress(job: *mut c_void) -> u32 {
    if job.is_null() {
        return 0;
    }
    unsafe {
        let job_ref = &*(job as *const TranscriberJob);
        job_ref.get_progress()
    }
}

/// 완료 여부 확인
/// 반환: 1=완료, 0=진행중
#[no_mangle]
pub extern "C" fn transcriber_is_finished(job: *mut c_void) -> i32 {
    if job.is_null() {
        return 1;
    }
    unsafe {
        let job_ref = &*(job as *const TranscriberJob);
        if job_ref.is_finished() { 1 } else { 0 }
    }
}

/// 에러 메시지 가져오기
/// out_error: null이면 에러 없음
/// 사용 후 transcriber_free_string()으로 해제
#[no_mangle]
pub extern "C" fn transcriber_get_error(
    job: *mut c_void,
    out_error: *mut *mut c_char,
) -> i32 {
    if job.is_null() || out_error.is_null() {
        return ErrorCode::NullPointer as i32;
    }
    unsafe {
        let job_ref = &*(job as *const TranscriberJob);
        match job_ref.get_error() {
            Some(msg) => {
                match CString::new(msg) {
                    Ok(c_str) => *out_error = c_str.into_raw(),
                    Err(_) => *out_error = std::ptr::null_mut(),
                }
            }
            None => *out_error = std::ptr::null_mut(),
        }
    }
    ErrorCode::Success as i32
}

/// 세그먼트 목록 가져오기
/// out_count: 세그먼트 수 반환
/// 반환: CTranscriptSegment 배열 포인터 (transcriber_free_segments로 해제)
/// 실패 시 null 반환
#[no_mangle]
pub extern "C" fn transcriber_get_segments(
    job: *mut c_void,
    out_count: *mut u32,
) -> *mut CTranscriptSegment {
    if job.is_null() || out_count.is_null() {
        return std::ptr::null_mut();
    }

    unsafe {
        let job_ref = &*(job as *const TranscriberJob);
        let segs = job_ref.get_segments();

        if segs.is_empty() {
            *out_count = 0;
            return std::ptr::null_mut();
        }

        let count = segs.len();
        let c_segs: Vec<CTranscriptSegment> = segs
            .into_iter()
            .map(|s| {
                let c_text = CString::new(s.text.clone()).unwrap_or_default();
                let text_len = c_text.to_bytes().len() as u32;
                let text_ptr = c_text.into_raw() as *const c_char;
                CTranscriptSegment {
                    start_ms: s.start_ms,
                    end_ms: s.end_ms,
                    text_ptr,
                    text_len,
                }
            })
            .collect();

        *out_count = count as u32;

        // 박스로 만들어 raw pointer 반환
        let boxed = c_segs.into_boxed_slice();
        Box::into_raw(boxed) as *mut CTranscriptSegment
    }
}

/// 세그먼트 배열 해제
#[no_mangle]
pub extern "C" fn transcriber_free_segments(ptr: *mut CTranscriptSegment, count: u32) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        // 각 세그먼트의 text_ptr 해제
        let segs = std::slice::from_raw_parts_mut(ptr, count as usize);
        for seg in segs.iter() {
            if !seg.text_ptr.is_null() {
                let _ = CString::from_raw(seg.text_ptr as *mut c_char);
            }
        }
        // 슬라이스 자체 해제
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(ptr, count as usize));
    }
}

/// 문자열 해제 (transcriber_get_error로 할당된 것)
#[no_mangle]
pub extern "C" fn transcriber_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            let _ = CString::from_raw(ptr);
        }
    }
}

/// 중단 요청 — whisper가 세그먼트 경계에서 처리를 중단함
/// Cancel 버튼에서 호출 (비동기 — 즉시 멈추지 않고 다음 세그먼트 후 중단)
#[no_mangle]
pub extern "C" fn transcriber_request_abort(job: *mut c_void) {
    if job.is_null() {
        return;
    }
    use std::sync::atomic::Ordering;
    unsafe {
        let job_ref = &*(job as *const TranscriberJob);
        job_ref.abort_requested.store(true, Ordering::SeqCst);
        eprintln!("[WHISPER] 중단 요청됨 — 다음 세그먼트 후 중단");
    }
}

/// TranscriberJob 파괴 — 완료/오류 후 반드시 호출
#[no_mangle]
pub extern "C" fn transcriber_destroy(job: *mut c_void) -> i32 {
    if job.is_null() {
        return ErrorCode::NullPointer as i32;
    }
    unsafe {
        // Arc::from_raw로 소유권 회수 → drop
        let _ = Arc::from_raw(job as *const TranscriberJob);
    }
    ErrorCode::Success as i32
}

// C-compatible 타입 정의
// C#과 공유되는 데이터 구조

use std::os::raw::c_char;

/// 에러 코드
pub const ERROR_SUCCESS: i32 = 0;
pub const ERROR_NULL_PTR: i32 = 1;
pub const ERROR_INVALID_PARAM: i32 = 2;
pub const ERROR_FFMPEG: i32 = 3;
pub const ERROR_IO: i32 = 4;
pub const ERROR_RENDER_FAILED: i32 = 5;
pub const ERROR_UNKNOWN: i32 = 99;

/// 에러 코드 Enum
#[repr(i32)]
pub enum ErrorCode {
    Success = 0,
    NullPointer = 1,
    InvalidParam = 2,
    Ffmpeg = 3,
    Io = 4,
    RenderFailed = 5,
    Unknown = 99,
}

/// C-compatible 에러 구조체
#[repr(C)]
pub struct CError {
    pub code: i32,
    pub message: *const c_char,
}

/// C-compatible 클립 구조체
#[repr(C)]
pub struct CClip {
    pub id: u64,
    pub start_time_ms: i64,
    pub duration_ms: i64,
    pub track_index: i32,
    pub clip_type: i32,  // 0=Video, 1=Audio, 2=Image
    pub file_path: *const c_char,
}

/// C-compatible 렌더 프레임 구조체
#[repr(C)]
pub struct CRenderFrame {
    pub width: u32,
    pub height: u32,
    pub format: i32,  // 0=RGBA, 1=RGB, 2=YUV420P
    pub data: *mut u8,
    pub data_len: usize,
}

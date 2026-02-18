// FFI (Foreign Function Interface) 모듈
// C# P/Invoke와 연동되는 C ABI 함수들

pub mod types;
pub mod timeline;
pub mod renderer;
pub mod exporter;
pub mod audio;
pub mod thumbnail;
pub mod audio_playback;
pub mod transcriber;

use std::ffi::CString;
use std::os::raw::c_char;

/// 문자열 메모리 해제
#[no_mangle]
pub extern "C" fn string_free(ptr: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            let _ = CString::from_raw(ptr);
        }
    }
}

/// Hello World 테스트 함수
#[no_mangle]
pub extern "C" fn hello_world() -> *mut c_char {
    let message = "Hello from Rust!";
    CString::new(message)
        .expect("CString::new failed")
        .into_raw()
}

/// 두 수를 더하는 테스트 함수
#[no_mangle]
pub extern "C" fn add_numbers(a: i32, b: i32) -> i32 {
    a + b
}

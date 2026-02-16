// VortexCut Rust 렌더링 엔진
// Rust + rusty_ffmpeg 기반 영상 편집 엔진

/// 디버그 로그 매크로 — `cargo build --features debug_log` 시에만 출력
/// 평소 릴리스 빌드에서는 컴파일 자체에서 제외됨
#[macro_export]
macro_rules! debug_log {
    ($($arg:tt)*) => {
        #[cfg(feature = "debug_log")]
        eprintln!($($arg)*);
    };
}

pub mod ffi;
pub mod ffmpeg;
pub mod timeline;
pub mod rendering;
pub mod encoding;
pub mod subtitle;
pub mod utils;
pub mod audio;

// FFI 함수들을 최상위에서 재export
pub use ffi::*;

#[cfg(test)]
mod tests {
    #[test]
    fn it_works() {
        assert_eq!(2 + 2, 4);
    }
}

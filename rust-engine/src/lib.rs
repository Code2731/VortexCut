// VortexCut Rust 렌더링 엔진
// Rust + rusty_ffmpeg 기반 영상 편집 엔진

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

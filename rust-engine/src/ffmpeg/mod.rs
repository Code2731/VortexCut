// FFmpeg 래퍼 모듈
// 비디오/오디오 디코딩/인코딩

pub mod decoder;

pub use decoder::{Decoder, Frame, PixelFormat, DecoderState, DecodeResult};

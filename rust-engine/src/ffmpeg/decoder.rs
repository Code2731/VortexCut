// FFmpeg Decoder 모듈 (더미 구현)
// TODO: 나중에 실제 rusty_ffmpeg 통합

use std::path::Path;

/// 비디오 프레임 데이터
#[derive(Debug, Clone)]
pub struct Frame {
    pub width: u32,
    pub height: u32,
    pub format: PixelFormat,
    pub data: Vec<u8>,
    pub timestamp_ms: i64,
}

/// 픽셀 포맷
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PixelFormat {
    RGBA,
    RGB,
    YUV420P,
}

/// 비디오 디코더
pub struct Decoder {
    file_path: String,
    width: u32,
    height: u32,
    fps: f64,
    duration_ms: i64,
}

impl Decoder {
    /// 비디오 파일 열기
    pub fn open(file_path: &Path) -> Result<Self, String> {
        // TODO: 실제 FFmpeg 구현
        // 지금은 더미 데이터 반환
        Ok(Self {
            file_path: file_path.to_string_lossy().to_string(),
            width: 1920,
            height: 1080,
            fps: 30.0,
            duration_ms: 10000, // 10초
        })
    }

    /// 비디오 정보 가져오기
    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    pub fn fps(&self) -> f64 {
        self.fps
    }

    pub fn duration_ms(&self) -> i64 {
        self.duration_ms
    }

    /// 특정 시간의 프레임 디코딩
    pub fn decode_frame(&mut self, timestamp_ms: i64) -> Result<Frame, String> {
        // TODO: 실제 FFmpeg 디코딩 구현
        // 지금은 더미 프레임 생성 (검은색)
        let size = (self.width * self.height * 4) as usize; // RGBA
        let data = vec![0u8; size];

        Ok(Frame {
            width: self.width,
            height: self.height,
            format: PixelFormat::RGBA,
            data,
            timestamp_ms,
        })
    }

    /// 다음 프레임 디코딩
    pub fn decode_next_frame(&mut self) -> Result<Option<Frame>, String> {
        // TODO: 실제 구현
        Ok(None)
    }

    /// 특정 시간으로 seek
    pub fn seek(&mut self, timestamp_ms: i64) -> Result<(), String> {
        // TODO: 실제 구현
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn test_decoder_open() {
        let path = PathBuf::from("test.mp4");
        let decoder = Decoder::open(&path);
        assert!(decoder.is_ok());
    }

    #[test]
    fn test_decoder_info() {
        let path = PathBuf::from("test.mp4");
        let decoder = Decoder::open(&path).unwrap();

        assert_eq!(decoder.width(), 1920);
        assert_eq!(decoder.height(), 1080);
        assert_eq!(decoder.fps(), 30.0);
        assert_eq!(decoder.duration_ms(), 10000);
    }

    #[test]
    fn test_decode_frame() {
        let path = PathBuf::from("test.mp4");
        let mut decoder = Decoder::open(&path).unwrap();

        let frame = decoder.decode_frame(1000);
        assert!(frame.is_ok());

        let frame = frame.unwrap();
        assert_eq!(frame.width, 1920);
        assert_eq!(frame.height, 1080);
        assert_eq!(frame.timestamp_ms, 1000);
        assert_eq!(frame.data.len(), 1920 * 1080 * 4);
    }
}

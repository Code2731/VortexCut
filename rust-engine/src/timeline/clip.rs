// 클립 모듈 - 타임라인에 배치되는 미디어 세그먼트

use std::path::PathBuf;

/// 클립 타입
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClipType {
    Video,
    Audio,
    Image,
}

/// 비디오 클립
#[derive(Debug, Clone)]
pub struct VideoClip {
    pub id: u64,
    pub file_path: PathBuf,
    pub start_time_ms: i64,    // 타임라인 상 시작 시간
    pub duration_ms: i64,       // 타임라인 상 지속 시간
    pub trim_start_ms: i64,     // 원본 파일에서 트림 시작
    pub trim_end_ms: i64,       // 원본 파일에서 트림 끝
    pub volume: f32,            // 0.0~2.0, 기본 1.0 (비디오 파일 내 오디오 볼륨)
    pub speed: f64,             // 0.25~4.0, 기본 1.0
}

impl VideoClip {
    /// 새 비디오 클립 생성
    pub fn new(id: u64, file_path: PathBuf, start_time_ms: i64, duration_ms: i64) -> Self {
        Self {
            id,
            file_path,
            start_time_ms,
            duration_ms,
            trim_start_ms: 0,
            trim_end_ms: duration_ms,
            volume: 1.0,
            speed: 1.0,
        }
    }

    /// 클립의 끝 시간
    pub fn end_time_ms(&self) -> i64 {
        self.start_time_ms + self.duration_ms
    }

    /// 클립이 특정 시간을 포함하는지 확인
    pub fn contains_time(&self, time_ms: i64) -> bool {
        time_ms >= self.start_time_ms && time_ms < self.end_time_ms()
    }

    /// 타임라인 시간을 원본 파일 시간으로 변환
    /// speed=2.0이면 타임라인 1초에 원본 2초 재생
    pub fn timeline_to_source_time(&self, timeline_time_ms: i64) -> Option<i64> {
        if !self.contains_time(timeline_time_ms) {
            return None;
        }

        let offset = timeline_time_ms - self.start_time_ms;
        Some(self.trim_start_ms + (offset as f64 * self.speed) as i64)
    }
}

/// 오디오 클립
#[derive(Debug, Clone)]
pub struct AudioClip {
    pub id: u64,
    pub file_path: PathBuf,
    pub start_time_ms: i64,
    pub duration_ms: i64,
    pub trim_start_ms: i64,
    pub trim_end_ms: i64,
    pub volume: f32,        // 0.0~2.0, 기본 1.0
    pub speed: f64,          // 0.25~4.0, 기본 1.0
    pub fade_in_ms: i64,     // 0 = 페이드 없음
    pub fade_out_ms: i64,    // 0 = 페이드 없음
}

impl AudioClip {
    /// 새 오디오 클립 생성
    pub fn new(id: u64, file_path: PathBuf, start_time_ms: i64, duration_ms: i64) -> Self {
        Self {
            id,
            file_path,
            start_time_ms,
            duration_ms,
            trim_start_ms: 0,
            trim_end_ms: duration_ms,
            volume: 1.0,
            speed: 1.0,
            fade_in_ms: 0,
            fade_out_ms: 0,
        }
    }

    /// 클립의 끝 시간
    pub fn end_time_ms(&self) -> i64 {
        self.start_time_ms + self.duration_ms
    }

    /// 클립이 특정 시간을 포함하는지 확인
    pub fn contains_time(&self, time_ms: i64) -> bool {
        time_ms >= self.start_time_ms && time_ms < self.end_time_ms()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_video_clip_creation() {
        let clip = VideoClip::new(1, PathBuf::from("test.mp4"), 0, 5000);
        assert_eq!(clip.id, 1);
        assert_eq!(clip.start_time_ms, 0);
        assert_eq!(clip.duration_ms, 5000);
        assert_eq!(clip.end_time_ms(), 5000);
    }

    #[test]
    fn test_clip_contains_time() {
        let clip = VideoClip::new(1, PathBuf::from("test.mp4"), 1000, 5000);

        assert!(!clip.contains_time(500));
        assert!(clip.contains_time(1000));
        assert!(clip.contains_time(3000));
        assert!(clip.contains_time(5999));
        assert!(!clip.contains_time(6000));
    }

    #[test]
    fn test_timeline_to_source_time() {
        let mut clip = VideoClip::new(1, PathBuf::from("test.mp4"), 2000, 3000);
        clip.trim_start_ms = 1000;
        clip.trim_end_ms = 4000;

        // 타임라인 2000ms = 원본 1000ms
        assert_eq!(clip.timeline_to_source_time(2000), Some(1000));
        // 타임라인 3000ms = 원본 2000ms
        assert_eq!(clip.timeline_to_source_time(3000), Some(2000));
        // 범위 밖
        assert_eq!(clip.timeline_to_source_time(1000), None);
        assert_eq!(clip.timeline_to_source_time(6000), None);
    }
}

// 타임라인 모듈 - 전체 프로젝트의 타임라인 관리

use super::track::{VideoTrack, AudioTrack};
use super::clip::{VideoClip, AudioClip};

/// 타임라인 - 비디오 편집 프로젝트의 핵심
#[derive(Debug, Clone)]
pub struct Timeline {
    pub width: u32,
    pub height: u32,
    pub fps: f64,
    pub video_tracks: Vec<VideoTrack>,
    pub audio_tracks: Vec<AudioTrack>,
    next_clip_id: u64,
    next_track_id: u64,
}

impl Timeline {
    /// 새 타임라인 생성
    pub fn new(width: u32, height: u32, fps: f64) -> Self {
        Self {
            width,
            height,
            fps,
            video_tracks: Vec::new(),
            audio_tracks: Vec::new(),
            next_clip_id: 1,
            next_track_id: 1,
        }
    }

    /// 비디오 트랙 추가
    pub fn add_video_track(&mut self) -> u64 {
        let id = self.next_track_id;
        self.next_track_id += 1;

        let index = self.video_tracks.len();
        self.video_tracks.push(VideoTrack::new(id, index));

        id
    }

    /// 오디오 트랙 추가
    pub fn add_audio_track(&mut self) -> u64 {
        let id = self.next_track_id;
        self.next_track_id += 1;

        let index = self.audio_tracks.len();
        self.audio_tracks.push(AudioTrack::new(id, index));

        id
    }

    /// 비디오 클립 추가
    pub fn add_video_clip(
        &mut self,
        track_id: u64,
        file_path: std::path::PathBuf,
        start_time_ms: i64,
        duration_ms: i64,
    ) -> Option<u64> {
        let track = self.video_tracks.iter_mut().find(|t| t.id == track_id)?;

        let clip_id = self.next_clip_id;
        self.next_clip_id += 1;

        let clip = VideoClip::new(clip_id, file_path, start_time_ms, duration_ms);
        track.add_clip(clip);

        Some(clip_id)
    }

    /// 오디오 클립 추가
    pub fn add_audio_clip(
        &mut self,
        track_id: u64,
        file_path: std::path::PathBuf,
        start_time_ms: i64,
        duration_ms: i64,
    ) -> Option<u64> {
        let track = self.audio_tracks.iter_mut().find(|t| t.id == track_id)?;

        let clip_id = self.next_clip_id;
        self.next_clip_id += 1;

        let clip = AudioClip::new(clip_id, file_path, start_time_ms, duration_ms);
        track.add_clip(clip);

        Some(clip_id)
    }

    /// 비디오 클립 제거
    pub fn remove_video_clip(&mut self, track_id: u64, clip_id: u64) -> bool {
        if let Some(track) = self.video_tracks.iter_mut().find(|t| t.id == track_id) {
            track.remove_clip(clip_id).is_some()
        } else {
            false
        }
    }

    /// 오디오 클립 제거
    pub fn remove_audio_clip(&mut self, track_id: u64, clip_id: u64) -> bool {
        if let Some(track) = self.audio_tracks.iter_mut().find(|t| t.id == track_id) {
            track.remove_clip(clip_id).is_some()
        } else {
            false
        }
    }

    /// 타임라인 총 길이 계산 (ms)
    pub fn duration_ms(&self) -> i64 {
        let video_max = self.video_tracks
            .iter()
            .flat_map(|t| &t.clips)
            .map(|c| c.end_time_ms())
            .max()
            .unwrap_or(0);

        let audio_max = self.audio_tracks
            .iter()
            .flat_map(|t| &t.clips)
            .map(|c| c.end_time_ms())
            .max()
            .unwrap_or(0);

        video_max.max(audio_max)
    }

    /// 트랙 뮤트 설정 (비디오 트랙 + 오디오 트랙 모두 검색)
    pub fn set_track_muted(&mut self, track_id: u64, muted: bool) -> bool {
        if let Some(track) = self.video_tracks.iter_mut().find(|t| t.id == track_id) {
            track.muted = muted;
            return true;
        }
        if let Some(track) = self.audio_tracks.iter_mut().find(|t| t.id == track_id) {
            track.muted = muted;
            return true;
        }
        false
    }

    /// 특정 시간에 활성화된 비디오 클립들 찾기 (모든 트랙)
    pub fn get_video_clips_at_time(&self, time_ms: i64) -> Vec<(&VideoTrack, &VideoClip)> {
        let mut clips = Vec::new();

        for track in &self.video_tracks {
            if let Some(clip) = track.get_clip_at_time(time_ms) {
                clips.push((track, clip));
            }
        }

        // 트랙 인덱스 순으로 정렬 (하단부터)
        clips.sort_by_key(|(track, _)| track.index);

        clips
    }

    /// 특정 시간에 활성화된 오디오 클립들 찾기 (모든 트랙)
    pub fn get_audio_clips_at_time(&self, time_ms: i64) -> Vec<&AudioClip> {
        self.audio_tracks
            .iter()
            .flat_map(|track| track.get_clips_at_time(time_ms))
            .collect()
    }

    /// 특정 시간에 오디오를 제공할 수 있는 모든 소스 (오디오 트랙 + 비디오 트랙)
    /// 비디오 파일에도 오디오 스트림이 있으므로, 비디오 클립도 AudioClip으로 변환하여 반환
    pub fn get_all_audio_sources_at_time(&self, time_ms: i64) -> Vec<AudioClip> {
        let mut sources = Vec::new();

        // 오디오 트랙의 클립
        for clip in self.get_audio_clips_at_time(time_ms) {
            sources.push(clip.clone());
        }

        // 비디오 트랙의 클립 → AudioClip으로 변환 (비디오 파일의 오디오 스트림 추출)
        for (_, video_clip) in self.get_video_clips_at_time(time_ms) {
            sources.push(AudioClip {
                id: video_clip.id,
                file_path: video_clip.file_path.clone(),
                start_time_ms: video_clip.start_time_ms,
                duration_ms: video_clip.duration_ms,
                trim_start_ms: video_clip.trim_start_ms,
                trim_end_ms: video_clip.trim_end_ms,
                volume: video_clip.volume,
                speed: video_clip.speed,
                fade_in_ms: 0,
                fade_out_ms: 0,
            });
        }

        sources
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn test_timeline_creation() {
        let timeline = Timeline::new(1920, 1080, 30.0);
        assert_eq!(timeline.width, 1920);
        assert_eq!(timeline.height, 1080);
        assert_eq!(timeline.fps, 30.0);
        assert_eq!(timeline.video_tracks.len(), 0);
        assert_eq!(timeline.audio_tracks.len(), 0);
    }

    #[test]
    fn test_add_tracks() {
        let mut timeline = Timeline::new(1920, 1080, 30.0);

        let video_track_id = timeline.add_video_track();
        let audio_track_id = timeline.add_audio_track();

        assert_eq!(timeline.video_tracks.len(), 1);
        assert_eq!(timeline.audio_tracks.len(), 1);
        assert_eq!(timeline.video_tracks[0].id, video_track_id);
        assert_eq!(timeline.audio_tracks[0].id, audio_track_id);
    }

    #[test]
    fn test_add_video_clip() {
        let mut timeline = Timeline::new(1920, 1080, 30.0);
        let track_id = timeline.add_video_track();

        let clip_id = timeline.add_video_clip(
            track_id,
            PathBuf::from("test.mp4"),
            0,
            5000,
        );

        assert!(clip_id.is_some());
        assert_eq!(timeline.video_tracks[0].clips.len(), 1);
        assert_eq!(timeline.video_tracks[0].clips[0].id, clip_id.unwrap());
    }

    #[test]
    fn test_remove_video_clip() {
        let mut timeline = Timeline::new(1920, 1080, 30.0);
        let track_id = timeline.add_video_track();
        let clip_id = timeline.add_video_clip(
            track_id,
            PathBuf::from("test.mp4"),
            0,
            5000,
        ).unwrap();

        assert_eq!(timeline.video_tracks[0].clips.len(), 1);

        let removed = timeline.remove_video_clip(track_id, clip_id);
        assert!(removed);
        assert_eq!(timeline.video_tracks[0].clips.len(), 0);
    }

    #[test]
    fn test_timeline_duration() {
        let mut timeline = Timeline::new(1920, 1080, 30.0);

        let video_track = timeline.add_video_track();
        let audio_track = timeline.add_audio_track();

        timeline.add_video_clip(video_track, PathBuf::from("v1.mp4"), 0, 5000);
        timeline.add_video_clip(video_track, PathBuf::from("v2.mp4"), 5000, 3000);
        timeline.add_audio_clip(audio_track, PathBuf::from("a1.mp3"), 0, 10000);

        assert_eq!(timeline.duration_ms(), 10000);
    }

    #[test]
    fn test_get_clips_at_time() {
        let mut timeline = Timeline::new(1920, 1080, 30.0);

        let track1 = timeline.add_video_track();
        let track2 = timeline.add_video_track();

        timeline.add_video_clip(track1, PathBuf::from("v1.mp4"), 0, 5000);
        timeline.add_video_clip(track2, PathBuf::from("v2.mp4"), 2000, 3000);

        let clips_at_3000 = timeline.get_video_clips_at_time(3000);
        assert_eq!(clips_at_3000.len(), 2);

        let clips_at_6000 = timeline.get_video_clips_at_time(6000);
        assert_eq!(clips_at_6000.len(), 0);
    }
}

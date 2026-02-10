// 렌더링 엔진 - Timeline을 실제 프레임으로 렌더링

use crate::timeline::{Timeline, VideoClip};
use crate::ffmpeg::{Decoder, Frame as DecoderFrame};
use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex};

/// 렌더링된 프레임 데이터
pub struct RenderedFrame {
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>, // RGBA 포맷
    pub timestamp_ms: i64,
}

/// 비디오 렌더러
pub struct Renderer {
    timeline: Arc<Mutex<Timeline>>,
    decoder_cache: HashMap<String, Decoder>, // 파일 경로 -> Decoder
}

impl Renderer {
    /// 새 렌더러 생성
    pub fn new(timeline: Arc<Mutex<Timeline>>) -> Self {
        Self {
            timeline,
            decoder_cache: HashMap::new(),
        }
    }

    /// 특정 시간의 프레임 렌더링
    pub fn render_frame(&mut self, timestamp_ms: i64) -> Result<RenderedFrame, String> {
        // Timeline 데이터 복사 (lock 해제를 위해)
        let (width, height, clips_to_render) = {
            let timeline = self.timeline.lock()
                .map_err(|e| format!("Failed to lock timeline: {}", e))?;

            let mut clips = Vec::new();

            // 비디오 트랙들을 순회하며 렌더링할 클립 수집
            for track in &timeline.video_tracks {
                if !track.enabled {
                    continue;
                }

                // 현재 timestamp에 해당하는 클립 찾기
                if let Some(clip) = track.get_clip_at_time(timestamp_ms) {
                    // 클립의 source timestamp 계산
                    if let Some(source_time_ms) = clip.timeline_to_source_time(timestamp_ms) {
                        clips.push((clip.clone(), source_time_ms));
                    }
                }
            }

            (timeline.width, timeline.height, clips)
        }; // timeline lock 해제

        // 빈 프레임 생성 (검은색 배경)
        let mut output_data = vec![0u8; (width * height * 4) as usize];

        // 수집한 클립들을 렌더링 (timeline lock 없이)
        for (clip, source_time_ms) in clips_to_render {
            let frame = self.decode_clip_frame(&clip, source_time_ms)?;

            // 프레임을 output에 합성 (알파 블렌딩)
            self.composite_frame(&frame.data, &mut output_data, width, height);
        }

        Ok(RenderedFrame {
            width,
            height,
            data: output_data,
            timestamp_ms,
        })
    }

    /// 클립의 프레임 디코딩 (캐시 사용)
    fn decode_clip_frame(&mut self, clip: &VideoClip, source_time_ms: i64) -> Result<DecoderFrame, String> {
        let file_path = clip.file_path.to_string_lossy().to_string();

        // 디코더가 캐시에 없으면 생성
        if !self.decoder_cache.contains_key(&file_path) {
            let decoder = Decoder::open(&clip.file_path)?;
            self.decoder_cache.insert(file_path.clone(), decoder);
        }

        // 디코더에서 프레임 가져오기
        let decoder = self.decoder_cache.get_mut(&file_path)
            .ok_or("Decoder not found in cache")?;

        decoder.decode_frame(source_time_ms)
    }

    /// 프레임을 output에 합성 (단순 덮어쓰기, 나중에 알파 블렌딩 추가)
    fn composite_frame(&self, source: &[u8], dest: &mut [u8], width: u32, height: u32) {
        let size = (width * height * 4) as usize;
        if source.len() == size && dest.len() == size {
            dest.copy_from_slice(source);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_renderer_create() {
        let timeline = Arc::new(Mutex::new(Timeline::new(1920, 1080, 30.0)));
        let _renderer = Renderer::new(timeline);
    }
}

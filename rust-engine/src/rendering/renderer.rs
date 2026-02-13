// 렌더링 엔진 - Timeline을 실제 프레임으로 렌더링
// 아키텍처: FrameCache + DecodeResult 기반 안전 렌더링

use crate::timeline::{Timeline, VideoClip};
use crate::ffmpeg::{Decoder, DecodeResult};
use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};

// ============================================================
// 프레임 캐시 (LRU)
// ============================================================

/// 캐시 엔트리
struct CacheEntry {
    file_path: String,
    source_time_ms: i64,
    frame: RenderedFrame,
}

/// LRU 프레임 캐시
struct FrameCache {
    entries: VecDeque<CacheEntry>,
    max_entries: usize,
    max_bytes: usize,
    current_bytes: usize,
    hit_count: u64,
    miss_count: u64,
}

impl FrameCache {
    fn new(max_entries: usize, max_bytes: usize) -> Self {
        Self {
            entries: VecDeque::new(),
            max_entries,
            max_bytes,
            current_bytes: 0,
            hit_count: 0,
            miss_count: 0,
        }
    }

    /// 캐시에서 프레임 조회 (히트 시 LRU 갱신)
    fn get(&mut self, file_path: &str, source_time_ms: i64) -> Option<&RenderedFrame> {
        // 캐시 검색
        let idx = self.entries.iter().position(|e| {
            e.file_path == file_path && e.source_time_ms == source_time_ms
        });

        match idx {
            Some(i) => {
                self.hit_count += 1;
                // LRU: 히트된 항목을 뒤로 이동 (가장 최근 사용)
                if i < self.entries.len() - 1 {
                    let entry = self.entries.remove(i).unwrap();
                    self.entries.push_back(entry);
                }
                self.entries.back().map(|e| &e.frame)
            }
            None => {
                self.miss_count += 1;
                None
            }
        }
    }

    /// 캐시에 프레임 저장
    fn put(&mut self, file_path: String, source_time_ms: i64, frame: RenderedFrame) {
        let frame_bytes = frame.data.len();

        // 이미 존재하면 갱신
        if let Some(i) = self.entries.iter().position(|e| {
            e.file_path == file_path && e.source_time_ms == source_time_ms
        }) {
            let old = self.entries.remove(i).unwrap();
            self.current_bytes -= old.frame.data.len();
        }

        // 용량 초과 시 LRU evict (가장 오래된 것부터)
        while (self.entries.len() >= self.max_entries || self.current_bytes + frame_bytes > self.max_bytes)
            && !self.entries.is_empty()
        {
            if let Some(evicted) = self.entries.pop_front() {
                self.current_bytes -= evicted.frame.data.len();
            }
        }

        self.current_bytes += frame_bytes;
        self.entries.push_back(CacheEntry {
            file_path,
            source_time_ms,
            frame,
        });
    }

    /// 캐시 전체 클리어
    fn clear(&mut self) {
        self.entries.clear();
        self.current_bytes = 0;
    }

    /// 통계 조회
    fn stats(&self) -> (u32, usize) {
        (self.entries.len() as u32, self.current_bytes)
    }
}

// ============================================================
// 렌더링된 프레임
// ============================================================

/// 렌더링된 프레임 데이터
#[derive(Clone)]
pub struct RenderedFrame {
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>, // RGBA 또는 YUV420P
    pub timestamp_ms: i64,
    /// Export 시 true: data는 YUV420P (색공간 변환 손실 없음)
    /// 프리뷰 시 false: data는 RGBA
    pub is_yuv: bool,
}

// ============================================================
// 렌더러
// ============================================================

/// 비디오 렌더러 (캐시 + DecodeResult 기반)
pub struct Renderer {
    timeline: Arc<Mutex<Timeline>>,
    decoder_cache: HashMap<String, Decoder>,
    frame_cache: FrameCache,
    /// 마지막 성공 렌더링 프레임 (fallback용)
    last_rendered_frame: Option<RenderedFrame>,
    /// 재생 모드: true일 때 forward_threshold를 5초로 올려 seek 대신 forward decode
    /// false(스크럽)일 때는 기본값(66ms) 유지 → 즉시 seek으로 정확한 위치 도달
    playback_mode: bool,
    /// Export용 출력 해상도 (None이면 프리뷰 960x540)
    export_resolution: Option<(u32, u32)>,
    /// 진단 카운터 (매 30프레임마다 출력)
    diag_total: u64,
    diag_cache_hit: u64,
    diag_decoded: u64,
    diag_eof: u64,
    diag_skipped: u64,
    diag_no_clip: u64,
    diag_error: u64,
}

/// 검은색 프레임 생성 (기본 960x540, Export 시 지정 해상도)
fn black_frame(timestamp_ms: i64) -> RenderedFrame {
    black_frame_with_size(960, 540, timestamp_ms)
}

/// 지정 크기의 검은색 프레임 생성
fn black_frame_with_size(width: u32, height: u32, timestamp_ms: i64) -> RenderedFrame {
    RenderedFrame {
        width,
        height,
        data: vec![0u8; (width * height * 4) as usize],
        timestamp_ms,
        is_yuv: false,
    }
}

/// Export용 검은색 YUV420P 프레임 생성
fn black_frame_yuv(width: u32, height: u32, timestamp_ms: i64) -> RenderedFrame {
    let y_size = (width * height) as usize;
    let uv_size = ((width / 2) * (height / 2)) as usize;
    // YUV420P: Y=0 (검정), U=V=128 (무채색)
    let mut data = vec![0u8; y_size + uv_size * 2];
    for i in y_size..y_size + uv_size * 2 {
        data[i] = 128;
    }
    RenderedFrame {
        width,
        height,
        data,
        timestamp_ms,
        is_yuv: true,
    }
}

impl Renderer {
    /// 새 렌더러 생성 (프리뷰용)
    pub fn new(timeline: Arc<Mutex<Timeline>>) -> Self {
        Self {
            timeline,
            decoder_cache: HashMap::new(),
            // 60프레임 캐시 (~120MB at 960x540 RGBA)
            frame_cache: FrameCache::new(60, 200 * 1024 * 1024),
            last_rendered_frame: None,
            playback_mode: false,
            export_resolution: None,
            diag_total: 0,
            diag_cache_hit: 0,
            diag_decoded: 0,
            diag_eof: 0,
            diag_skipped: 0,
            diag_no_clip: 0,
            diag_error: 0,
        }
    }

    /// Export 전용 렌더러 생성
    /// - 프리뷰 Renderer와 완전히 격리 (Mutex 경합 없음)
    /// - 캐시 최소화 (순차 인코딩이므로 5프레임만)
    /// - 지정 해상도로 디코딩
    pub fn new_for_export(timeline: Arc<Mutex<Timeline>>, width: u32, height: u32) -> Self {
        Self {
            timeline,
            decoder_cache: HashMap::new(),
            // Export: 캐시 최소 (순차 인코딩이라 재사용 거의 없음)
            frame_cache: FrameCache::new(5, 50 * 1024 * 1024),
            last_rendered_frame: None,
            playback_mode: true, // forward decode 모드 (순차 접근)
            export_resolution: Some((width, height)),
            diag_total: 0,
            diag_cache_hit: 0,
            diag_decoded: 0,
            diag_eof: 0,
            diag_skipped: 0,
            diag_no_clip: 0,
            diag_error: 0,
        }
    }

    /// 재생 모드 설정: 재생 시작 시 true, 정지 시 false
    /// 재생 모드: forward_threshold=5000ms (seek 대신 forward decode → 빠름)
    /// 스크럽 모드: forward_threshold=기본값 (즉시 seek → 정확한 위치)
    pub fn set_playback_mode(&mut self, playback: bool) {
        self.playback_mode = playback;
        let threshold = if playback { 5000 } else { 100 }; // 재생: 5초, 스크럽: 100ms
        for decoder in self.decoder_cache.values_mut() {
            decoder.set_forward_threshold(threshold);
        }
        if playback {
            // 재생 시작 시 EOF 상태 디코더 정리 (forward decode 가능하도록)
            let error_keys: Vec<String> = self.decoder_cache.iter()
                .filter(|(_, d)| d.state() == crate::ffmpeg::DecoderState::Error)
                .map(|(k, _)| k.clone())
                .collect();
            for key in error_keys {
                self.decoder_cache.remove(&key);
            }
        }
    }

    /// 특정 시간의 프레임 렌더링 (캐시 + DecodeResult 안전 처리)
    pub fn render_frame(&mut self, timestamp_ms: i64) -> Result<RenderedFrame, String> {
        self.diag_total += 1;
        let render_start = std::time::Instant::now();

        // Timeline 데이터 복사 (lock 최소화)
        let clips_to_render = {
            let timeline = self.timeline.lock()
                .map_err(|e| format!("Failed to lock timeline: {}", e))?;

            let mut clips = Vec::new();

            for track in &timeline.video_tracks {
                if !track.enabled {
                    continue;
                }

                if let Some(clip) = track.get_clip_at_time(timestamp_ms) {
                    if let Some(source_time_ms) = clip.timeline_to_source_time(timestamp_ms) {
                        clips.push((clip.clone(), source_time_ms));
                    }
                }
            }

            clips
        }; // timeline lock 해제

        // 클립이 없으면 검은색 프레임 반환
        if clips_to_render.is_empty() {
            self.diag_no_clip += 1;
            self.print_diag_if_needed(timestamp_ms);
            return Ok(match self.export_resolution {
                Some((w, h)) => black_frame_yuv(w, h, timestamp_ms),
                None => black_frame(timestamp_ms),
            });
        }

        // 첫 번째 클립 렌더링
        let (clip, source_time_ms) = &clips_to_render[0];
        let file_path = clip.file_path.to_string_lossy().to_string();

        // 1단계: 캐시 조회 (.cloned()로 즉시 소유권 획득 → 가변 참조 해제)
        if let Some(mut frame) = self.frame_cache.get(&file_path, *source_time_ms).cloned() {
            frame.timestamp_ms = timestamp_ms;
            self.diag_cache_hit += 1;
            self.print_diag_if_needed(timestamp_ms);
            return Ok(frame);
        }

        // 2단계: 디코딩
        let decode_start = std::time::Instant::now();
        let result = self.decode_clip_frame(clip, *source_time_ms);
        let decode_elapsed = decode_start.elapsed().as_millis();

        // 처음 10프레임 또는 50ms 이상 걸린 경우 로그
        if self.diag_total <= 10 || decode_elapsed > 50 {
            eprintln!(
                "[RENDER] t={}ms src={}ms decode={}ms total_frames={}",
                timestamp_ms, source_time_ms, decode_elapsed, self.diag_total
            );
        }

        match result {
            Ok(decode_result) => {
                match decode_result {
                    DecodeResult::Frame(frame) => {
                        self.diag_decoded += 1;
                        let is_yuv = frame.format == crate::ffmpeg::PixelFormat::YUV420P;
                        let rendered = RenderedFrame {
                            width: frame.width,
                            height: frame.height,
                            data: frame.data,
                            timestamp_ms,
                            is_yuv,
                        };
                        // 캐시에 저장
                        self.frame_cache.put(file_path, *source_time_ms, rendered.clone());
                        self.last_rendered_frame = Some(rendered.clone());
                        self.print_diag_if_needed(timestamp_ms);
                        Ok(rendered)
                    }
                    DecodeResult::FrameSkipped => {
                        self.diag_skipped += 1;
                        self.print_diag_if_needed(timestamp_ms);
                        // 프레임 스킵 → 마지막 렌더링 프레임 반환 (재생 중단 방지)
                        Ok(self.last_rendered_frame.clone().unwrap_or_else(|| {
                            match self.export_resolution {
                                Some((w, h)) => black_frame_yuv(w, h, timestamp_ms),
                                None => black_frame(timestamp_ms),
                            }
                        }))
                    }
                    DecodeResult::EndOfStream(frame) => {
                        self.diag_eof += 1;
                        self.print_diag_if_needed(timestamp_ms);
                        let is_yuv = frame.format == crate::ffmpeg::PixelFormat::YUV420P;
                        let rendered = RenderedFrame {
                            width: frame.width,
                            height: frame.height,
                            data: frame.data,
                            timestamp_ms,
                            is_yuv,
                        };
                        self.last_rendered_frame = Some(rendered.clone());
                        Ok(rendered)
                    }
                    DecodeResult::EndOfStreamEmpty => {
                        self.diag_eof += 1;
                        self.print_diag_if_needed(timestamp_ms);
                        Ok(self.last_rendered_frame.clone().unwrap_or_else(|| {
                            match self.export_resolution {
                                Some((w, h)) => black_frame_yuv(w, h, timestamp_ms),
                                None => black_frame(timestamp_ms),
                            }
                        }))
                    }
                }
            }
            Err(e) => {
                self.diag_error += 1;
                self.print_diag_if_needed(timestamp_ms);
                eprintln!("Decode error at {}ms: {}", timestamp_ms, e);
                // 에러 시에도 마지막 프레임 반환 (재생 중단 방지)
                Ok(self.last_rendered_frame.clone().unwrap_or_else(|| {
                    match self.export_resolution {
                        Some((w, h)) => black_frame_yuv(w, h, timestamp_ms),
                        None => black_frame(timestamp_ms),
                    }
                }))
            }
        }
    }

    /// 진단 통계 출력 (30프레임=~1초마다)
    fn print_diag_if_needed(&self, last_ts: i64) {
        if self.diag_total % 30 == 0 {
            eprintln!(
                "[RENDER DIAG] t={}ms | total={} cache={} decode={} eof={} skip={} noclip={} err={}",
                last_ts,
                self.diag_total,
                self.diag_cache_hit,
                self.diag_decoded,
                self.diag_eof,
                self.diag_skipped,
                self.diag_no_clip,
                self.diag_error
            );
        }
    }

    /// 클립의 프레임 디코딩 (DecodeResult 반환)
    /// 에러 시 디코더 재생성 1회 재시도 (corrupted state 복구)
    fn decode_clip_frame(&mut self, clip: &VideoClip, source_time_ms: i64) -> Result<DecodeResult, String> {
        let file_path = clip.file_path.to_string_lossy().to_string();

        // Error 상태 디코더는 제거 후 재생성 (복구 불가능 상태 탈출)
        if let Some(decoder) = self.decoder_cache.get(&file_path) {
            if decoder.state() == crate::ffmpeg::DecoderState::Error {
                eprintln!("[DECODER] Error state, recreating: {}", file_path);
                self.decoder_cache.remove(&file_path);
            }
        }

        // 디코더가 캐시에 없으면 생성 (현재 모드의 forward_threshold 적용)
        let threshold = if self.playback_mode { 5000 } else { 100 };
        if !self.decoder_cache.contains_key(&file_path) {
            // Export: LANCZOS 고품질, 프리뷰: FAST_BILINEAR
            let mut decoder = match self.export_resolution {
                Some((w, h)) => Decoder::open_for_export(&clip.file_path, w, h)?,
                None => Decoder::open(&clip.file_path)?,
            };
            decoder.set_forward_threshold(threshold);
            self.decoder_cache.insert(file_path.clone(), decoder);
        }

        let decoder = self.decoder_cache.get_mut(&file_path)
            .ok_or("Decoder not found in cache")?;

        match decoder.decode_frame(source_time_ms) {
            Ok(result) => Ok(result),
            Err(e) => {
                eprintln!("[DECODER] Decode error at {}ms: {}, recreating decoder", source_time_ms, e);
                self.decoder_cache.remove(&file_path);

                let mut new_decoder = match self.export_resolution {
                    Some((w, h)) => Decoder::open_for_export(&clip.file_path, w, h)
                        .map_err(|e2| format!("Decoder recreate failed: {}", e2))?,
                    None => Decoder::open(&clip.file_path)
                        .map_err(|e2| format!("Decoder recreate failed: {}", e2))?,
                };
                new_decoder.set_forward_threshold(threshold);
                self.decoder_cache.insert(file_path.clone(), new_decoder);

                let decoder = self.decoder_cache.get_mut(&file_path)
                    .ok_or("Decoder not found after recreate")?;

                decoder.decode_frame(source_time_ms)
            }
        }
    }

    /// 캐시 클리어 (클립 편집 시 호출)
    pub fn clear_cache(&mut self) {
        self.frame_cache.clear();
    }

    /// 캐시 통계 조회
    pub fn cache_stats(&self) -> (u32, usize) {
        self.frame_cache.stats()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn test_renderer_create() {
        let timeline = Arc::new(Mutex::new(Timeline::new(1920, 1080, 30.0)));
        let _renderer = Renderer::new(timeline);
    }

    #[test]
    fn test_frame_cache_lru() {
        let mut cache = FrameCache::new(3, 100 * 1024 * 1024);

        // 3개 프레임 추가
        for i in 0..3 {
            cache.put("test.mp4".to_string(), i * 33, RenderedFrame {
                width: 960, height: 540, data: vec![0u8; 100], is_yuv: false, timestamp_ms: i * 33,
            });
        }
        assert_eq!(cache.entries.len(), 3);

        // 4번째 추가 → LRU eviction (가장 오래된 0ms 제거)
        cache.put("test.mp4".to_string(), 99, RenderedFrame {
            width: 960, height: 540, data: vec![0u8; 100], is_yuv: false, timestamp_ms: 99,
        });
        assert_eq!(cache.entries.len(), 3);
        // 0ms는 evict됨
        assert!(cache.get("test.mp4", 0).is_none());
        // 33ms, 66ms, 99ms는 존재
        assert!(cache.get("test.mp4", 33).is_some());
        assert!(cache.get("test.mp4", 66).is_some());
        assert!(cache.get("test.mp4", 99).is_some());
    }

    #[test]
    fn test_frame_cache_hit_miss() {
        let mut cache = FrameCache::new(10, 100 * 1024 * 1024);

        cache.put("test.mp4".to_string(), 0, RenderedFrame {
            width: 960, height: 540, data: vec![0u8; 100], is_yuv: false, timestamp_ms: 0,
        });

        // 히트
        assert!(cache.get("test.mp4", 0).is_some());
        assert_eq!(cache.hit_count, 1);
        assert_eq!(cache.miss_count, 0);

        // 미스
        assert!(cache.get("test.mp4", 100).is_none());
        assert_eq!(cache.hit_count, 1);
        assert_eq!(cache.miss_count, 1);
    }

    #[test]
    fn test_black_frame() {
        let frame = black_frame(1000);
        assert_eq!(frame.width, 960);
        assert_eq!(frame.height, 540);
        assert_eq!(frame.data.len(), (960 * 540 * 4) as usize);
        assert!(frame.data.iter().all(|&b| b == 0));
    }

    #[test]
    fn test_renderer_with_real_video() {
        let video_path = PathBuf::from(r"C:\Users\USER\Videos\드론 대응 2.75인치 로켓 '비궁'으로 유도키트 개발, 사우디 기술협력 추진.mp4");

        if !video_path.exists() {
            println!("Test video file not found, skipping test");
            return;
        }

        println!("\n=== Renderer Test ===");

        let timeline = Arc::new(Mutex::new(Timeline::new(1920, 1080, 30.0)));

        let track_id = {
            let mut tl = timeline.lock().unwrap();
            tl.add_video_track()
        };

        let _clip_id = {
            let mut tl = timeline.lock().unwrap();
            tl.add_video_clip(track_id, video_path.clone(), 0, 5000)
                .expect("Failed to add video clip")
        };

        let mut renderer = Renderer::new(timeline.clone());

        let timestamps = [0i64, 1000, 2000];
        for timestamp in timestamps {
            match renderer.render_frame(timestamp) {
                Ok(frame) => {
                    assert!(frame.width > 0);
                    assert!(frame.height > 0);
                    assert!(!frame.data.is_empty());
                }
                Err(e) => {
                    panic!("Failed to render frame at {}ms: {}", timestamp, e);
                }
            }
        }

        // 캐시 통계 확인
        let (cached, bytes) = renderer.cache_stats();
        println!("Cache: {} frames, {} bytes", cached, bytes);
        assert!(cached > 0);
    }
}

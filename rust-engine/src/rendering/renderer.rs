// 렌더링 엔진 - Timeline을 실제 프레임으로 렌더링
// 아키텍처: FrameCache + DecodeResult 기반 안전 렌더링

use crate::timeline::{Timeline, VideoClip};
use crate::timeline::track::TransitionInfo;
use crate::ffmpeg::{Decoder, DecodeResult};
use crate::rendering::effects::{EffectParams, apply_effects};
use crate::rendering::transitions::apply_transition;
use crate::subtitle::overlay::{yuv420p_to_rgba, rgba_to_yuv420p};
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
    /// 클립별 이펙트 파라미터
    clip_effects: HashMap<u64, EffectParams>,
    /// 직전 프레임 렌더 소요 시간 (ms) — 적응형 프레임 스킵 판정용
    last_render_elapsed_ms: u64,
    /// 진단 카운터 (매 30프레임마다 출력)
    diag_total: u64,
    diag_cache_hit: u64,
    diag_decoded: u64,
    diag_eof: u64,
    diag_skipped: u64,
    diag_no_clip: u64,
    diag_error: u64,
    diag_transition: u64,
    diag_transition_skip: u64,
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
            clip_effects: HashMap::new(),
            last_render_elapsed_ms: 0,
            diag_total: 0,
            diag_cache_hit: 0,
            diag_decoded: 0,
            diag_eof: 0,
            diag_skipped: 0,
            diag_no_clip: 0,
            diag_error: 0,
            diag_transition: 0,
            diag_transition_skip: 0,
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
            clip_effects: HashMap::new(),
            last_render_elapsed_ms: 0,
            diag_total: 0,
            diag_cache_hit: 0,
            diag_decoded: 0,
            diag_eof: 0,
            diag_skipped: 0,
            diag_no_clip: 0,
            diag_error: 0,
            diag_transition: 0,
            diag_transition_skip: 0,
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

    /// 프리뷰 시 proxy, Export 시 원본 경로 반환
    fn video_path_for_decode(&self, clip: &VideoClip) -> std::path::PathBuf {
        // Export 모드: 항상 원본 (최종 품질 보장)
        if self.export_resolution.is_some() {
            return clip.file_path.clone();
        }
        // 프리뷰/스크럽: proxy 있으면 proxy 사용
        if let Some(ref proxy) = clip.proxy_path {
            if proxy.exists() {
                return proxy.clone();
            }
        }
        clip.file_path.clone()
    }

    /// 단일 클립 디코딩 + 이펙트 적용 (RGBA 반환, 트랜지션 블렌딩 전처리용)
    fn decode_and_render_clip(&mut self, clip: &VideoClip, source_time_ms: i64, timestamp_ms: i64) -> Option<RenderedFrame> {
        let decode_path = self.video_path_for_decode(clip);
        let file_path = decode_path.to_string_lossy().to_string();

        // 캐시 조회
        if let Some(frame) = self.frame_cache.get(&file_path, source_time_ms).cloned() {
            return Some(frame);
        }

        // 디코딩
        let result = self.decode_clip_frame(clip, source_time_ms);
        match result {
            Ok(DecodeResult::Frame(frame)) | Ok(DecodeResult::EndOfStream(frame)) => {
                let is_yuv = frame.format == crate::ffmpeg::PixelFormat::YUV420P;
                let mut rendered = RenderedFrame {
                    width: frame.width,
                    height: frame.height,
                    data: if is_yuv {
                        yuv420p_to_rgba(&frame.data, frame.width, frame.height)
                    } else {
                        frame.data
                    },
                    timestamp_ms,
                    is_yuv: false, // 항상 RGBA (블렌딩용)
                };

                // 이펙트 적용
                if let Some(params) = self.clip_effects.get(&clip.id) {
                    if !params.is_default() {
                        apply_effects(&mut rendered.data, rendered.width, rendered.height, params);
                    }
                }

                if !self.playback_mode {
                    self.frame_cache.put(file_path, source_time_ms, rendered.clone());
                }
                Some(rendered)
            }
            _ => None,
        }
    }

    /// 특정 시간의 프레임 렌더링 (캐시 + DecodeResult 안전 처리)
    pub fn render_frame(&mut self, timestamp_ms: i64) -> Result<RenderedFrame, String> {
        self.diag_total += 1;
        let render_start = std::time::Instant::now();

        // Timeline 데이터 복사 (non-blocking lock → 오디오 fill thread와 경합 시 프레임 스킵)
        // 최적화: 최상위 트랙 클립 1개만 clone (멀티트랙에서 불필요한 clone 제거)
        let (clips_to_render, transitions) = {
            let timeline = match self.timeline.try_lock() {
                Ok(tl) => tl,
                Err(_) => {
                    // Timeline busy (오디오 fill thread가 lock 보유 중) → 프레임 스킵
                    self.diag_skipped += 1;
                    return Ok(self.last_rendered_frame.clone().unwrap_or_else(|| {
                        match self.export_resolution {
                            Some((w, h)) => black_frame_yuv(w, h, timestamp_ms),
                            None => black_frame(timestamp_ms),
                        }
                    }));
                }
            };

            let mut clips: Vec<(crate::timeline::VideoClip, i64)> = Vec::with_capacity(1);
            let mut transitions: Vec<TransitionInfo> = Vec::new();

            for track in &timeline.video_tracks {
                if !track.enabled {
                    continue;
                }

                // 트랜지션 먼저 확인 (겹치는 2클립)
                if let Some(info) = track.get_transition_at_time(timestamp_ms) {
                    transitions.push(info);
                } else if clips.is_empty() {
                    // 최상위 트랙 클립만 수집 (이미 찾았으면 건너뜀)
                    if let Some(clip) = track.get_clip_at_time(timestamp_ms) {
                        if let Some(source_time_ms) = clip.timeline_to_source_time(timestamp_ms) {
                            clips.push((clip.clone(), source_time_ms));
                        }
                    }
                }
            }

            (clips, transitions)
        }; // timeline lock 해제

        // 클립도 트랜지션도 없으면 검은색 프레임
        if clips_to_render.is_empty() && transitions.is_empty() {
            self.diag_no_clip += 1;
            self.print_diag_if_needed(timestamp_ms);
            return Ok(match self.export_resolution {
                Some((w, h)) => black_frame_yuv(w, h, timestamp_ms),
                None => black_frame(timestamp_ms),
            });
        }

        // 트랜지션 처리 (있으면 우선)
        if !transitions.is_empty() {
            let info = &transitions[0];

            // 적응형 프레임 스킵: 직전 프레임이 28ms 이상 걸렸으면
            // 트랜지션(2x 디코딩) 대신 이전 프레임 재사용 → 프레임 지연 누적 방지
            // Export는 스킵하지 않음 (품질 우선)
            if self.playback_mode && self.export_resolution.is_none()
                && self.last_render_elapsed_ms > 28
            {
                if let Some(ref frame) = self.last_rendered_frame {
                    let mut skipped = frame.clone();
                    skipped.timestamp_ms = timestamp_ms;
                    self.diag_transition_skip += 1;
                    self.last_render_elapsed_ms = 0; // 다음 프레임은 렌더
                    self.print_diag_if_needed(timestamp_ms);
                    return Ok(skipped);
                }
            }

            let out_src = info.outgoing.timeline_to_source_time(timestamp_ms);
            let in_src = info.incoming.timeline_to_source_time(timestamp_ms);

            if let (Some(out_src), Some(in_src)) = (out_src, in_src) {
                // .clone() 제거: info.outgoing/incoming은 TransitionInfo 내 소유 데이터
                // decode_and_render_clip은 &VideoClip만 필요 → 불필요한 PathBuf clone 제거
                let out_frame = self.decode_and_render_clip(&info.outgoing, out_src, timestamp_ms);
                let in_frame = self.decode_and_render_clip(&info.incoming, in_src, timestamp_ms);

                if let (Some(mut out_f), Some(in_f)) = (out_frame, in_frame) {
                    // RGBA 블렌딩
                    apply_transition(
                        &mut out_f.data,
                        &in_f.data,
                        out_f.width,
                        out_f.height,
                        info.progress,
                        info.transition_type,
                    );

                    // Export 시 YUV 변환
                    if self.export_resolution.is_some() {
                        let yuv = rgba_to_yuv420p(&out_f.data, out_f.width, out_f.height);
                        out_f.data = yuv;
                        out_f.is_yuv = true;
                    }

                    // clone+move 패턴: return용 clone 1회, last_rendered에 move
                    let return_frame = out_f.clone();
                    self.last_rendered_frame = Some(out_f);
                    self.diag_transition += 1;
                    self.last_render_elapsed_ms = render_start.elapsed().as_millis() as u64;
                    self.print_diag_if_needed(timestamp_ms);
                    return Ok(return_frame);
                }
            }
        }

        // 단일 클립 렌더링 (기존 경로)
        if clips_to_render.is_empty() {
            self.diag_no_clip += 1;
            self.print_diag_if_needed(timestamp_ms);
            return Ok(match self.export_resolution {
                Some((w, h)) => black_frame_yuv(w, h, timestamp_ms),
                None => black_frame(timestamp_ms),
            });
        }

        let (clip, source_time_ms) = &clips_to_render[0];
        let decode_path = self.video_path_for_decode(clip);
        let file_path = decode_path.to_string_lossy().to_string();

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
            debug_log!(
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
                        let mut rendered = RenderedFrame {
                            width: frame.width,
                            height: frame.height,
                            data: frame.data,
                            timestamp_ms,
                            is_yuv,
                        };
                        // 이펙트 적용 (RGBA 프리뷰만, YUV Export는 건너뜀)
                        if !rendered.is_yuv {
                            if let Some(params) = self.clip_effects.get(&clip.id) {
                                if !params.is_default() {
                                    apply_effects(&mut rendered.data, rendered.width, rendered.height, params);
                                }
                            }
                        }
                        // 캐시 저장: 재생 모드에서는 건너뜀 (순차 프레임 = 캐시 히트 없음, clone 2MB 낭비 방지)
                        if !self.playback_mode {
                            self.frame_cache.put(file_path, *source_time_ms, rendered.clone());
                        }
                        // last_rendered: clone 대신 반환 프레임을 clone하고 원본은 move
                        let return_frame = rendered.clone();
                        self.last_rendered_frame = Some(rendered);
                        self.last_render_elapsed_ms = render_start.elapsed().as_millis() as u64;
                        self.print_diag_if_needed(timestamp_ms);
                        Ok(return_frame)
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
                        let return_frame = rendered.clone();
                        self.last_rendered_frame = Some(rendered);
                        Ok(return_frame)
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
            debug_log!(
                "[RENDER DIAG] t={}ms | total={} cache={} decode={} trans={} trans_skip={} eof={} skip={} noclip={} err={} last={}ms",
                last_ts,
                self.diag_total,
                self.diag_cache_hit,
                self.diag_decoded,
                self.diag_transition,
                self.diag_transition_skip,
                self.diag_eof,
                self.diag_skipped,
                self.diag_no_clip,
                self.diag_error,
                self.last_render_elapsed_ms
            );
        }
    }

    /// 클립의 프레임 디코딩 (DecodeResult 반환)
    /// 에러 시 디코더 재생성 1회 재시도 (corrupted state 복구)
    fn decode_clip_frame(&mut self, clip: &VideoClip, source_time_ms: i64) -> Result<DecodeResult, String> {
        let decode_path = self.video_path_for_decode(clip);
        let file_path = decode_path.to_string_lossy().to_string();

        // Error 상태 디코더는 제거 후 재생성 (복구 불가능 상태 탈출)
        if let Some(decoder) = self.decoder_cache.get(&file_path) {
            if decoder.state() == crate::ffmpeg::DecoderState::Error {
                debug_log!("[DECODER] Error state, recreating: {}", file_path);
                self.decoder_cache.remove(&file_path);
            }
        }

        // 디코더가 캐시에 없으면 생성 (현재 모드의 forward_threshold 적용)
        let threshold = if self.playback_mode { 5000 } else { 100 };
        if !self.decoder_cache.contains_key(&file_path) {
            // Export: LANCZOS 고품질 (원본), 프리뷰: FAST_BILINEAR (proxy 또는 원본)
            let mut decoder = match self.export_resolution {
                Some((w, h)) => Decoder::open_for_export(&decode_path, w, h)?,
                None => Decoder::open(&decode_path)?,
            };
            decoder.set_forward_threshold(threshold);
            self.decoder_cache.insert(file_path.clone(), decoder);
        }

        let decoder = self.decoder_cache.get_mut(&file_path)
            .ok_or("Decoder not found in cache")?;

        match decoder.decode_frame(source_time_ms) {
            Ok(result) => Ok(result),
            Err(e) => {
                debug_log!("[DECODER] Decode error at {}ms: {}, recreating decoder", source_time_ms, e);
                self.decoder_cache.remove(&file_path);

                let mut new_decoder = match self.export_resolution {
                    Some((w, h)) => Decoder::open_for_export(&decode_path, w, h)
                        .map_err(|e2| format!("Decoder recreate failed: {}", e2))?,
                    None => Decoder::open(&decode_path)
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

    /// 클립 이펙트 설정 (C# Slider 변경 시 호출)
    pub fn set_clip_effects(&mut self, clip_id: u64, params: EffectParams) {
        if params.is_default() {
            self.clip_effects.remove(&clip_id);
        } else {
            self.clip_effects.insert(clip_id, params);
        }
        // 캐시 클리어 — 이펙트가 변경되면 캐시된 프레임도 무효화
        self.frame_cache.clear();
    }

    /// 클립 이펙트 제거
    pub fn clear_clip_effects(&mut self, clip_id: u64) {
        self.clip_effects.remove(&clip_id);
        self.frame_cache.clear();
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

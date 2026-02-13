# 재생 프리즈 버그 분석 문서

## 프로젝트 개요
- **VortexCut**: Avalonia UI (C#) + Rust FFI (P/Invoke) 기반 비디오 편집기
- **구조**: C# UI → P/Invoke → Rust Engine (FFmpeg 디코딩)
- **Rust 크레이트**: `ffmpeg-next` (ffmpeg-sys-next 바인딩)

## 버그 증상
1. 동영상 임포트 후 재생 버튼 클릭
2. **프리뷰 영상이 거의 업데이트되지 않음** (1초에 2-3프레임)
3. 타임라인 플레이헤드는 움직이지만 프리뷰 화면은 "프리즈"처럼 보임

## 핵심 아키텍처

### 재생 파이프라인
```
[C# Timer 30fps] → OnPlaybackTick (ThreadPool)
  → Dispatcher.Post(시간 업데이트)  ← UI 스레드
  → PlaybackRenderAsync(timestamp)  ← fire-and-forget
    → Task.Run(() => RenderService.RenderFrame(ts))  ← ThreadPool
      → P/Invoke: renderer_render_frame(ptr, ts, ...)  ← Rust FFI
        → Mutex<Renderer>.try_lock()
          → Renderer.render_frame(ts)
            → Timeline.lock() → get_clips_at_time()
            → FrameCache.get() 또는 Decoder.decode_frame()
    → Dispatcher.UIThread.InvokeAsync(() => UpdateBitmap(...))
```

### Rust Mutex 구조
```rust
// FFI 레이어: renderer_render_frame
let renderer_mutex = &*(renderer as *const Mutex<Renderer>);
match renderer_mutex.try_lock() {  // ← 비차단 잠금 시도
    Ok(mut renderer) => renderer.render_frame(timestamp_ms),
    Err(_) => { *out_width = 0; return Success; }  // Mutex 사용 중 → null
}
```
- `try_lock()` 사용: Mutex가 사용 중이면 **즉시 null 반환** (프레임 스킵)
- `lock()` 아님: 대기하지 않음

## 진단 로그 (실제)

```
[10:59:34.253] [PLAY] Started from 127300ms
[10:59:34.293] [TICK] #1 t=127341ms (elapsed=41ms)
[10:59:34.332] [RENDER] t=127371ms: null (Mutex busy or width=0)   ← tick #2
[10:59:34.368] [RENDER] t=127404ms: null (Mutex busy or width=0)   ← tick #3
[10:59:34.402] [RENDER] t=127438ms: null (Mutex busy or width=0)   ← tick #4
[10:59:34.435] [RENDER] t=127475ms: null (Mutex busy or width=0)   ← tick #5
[10:59:34.469] [RENDER] t=127508ms: null (Mutex busy or width=0)   ← tick #6
...모든 틱에서 null...
[10:59:34.665] [RENDER] t=127341ms: OK 960x540   ← tick #1 성공! (372ms 후)
...그 후 다시 모든 틱에서 null 반복...
```

### 패턴
- **30fps 타이머**가 매 33ms마다 `RenderFrameAsync()`를 fire-and-forget으로 호출
- **30개 Task가 동시에** Rust Mutex를 `try_lock()` 시도
- **1개만 성공**, 나머지 29개는 즉시 null 반환
- 성공한 1개는 **370ms 소요** (그동안 다른 모든 시도 실패)
- 결과: **실질 2-3fps** → "프리즈"

## 분석된 근본 원인 (2개)

### 원인 1: Mutex 기아 (Starvation)
- fire-and-forget 패턴으로 30개 Task가 동시에 Mutex 경합
- `try_lock()`이므로 실패 시 즉시 null → 대부분의 렌더 요청이 버려짐

**시도한 수정 (C#)**: 단일 렌더 슬롯 패턴
```csharp
private int _playbackRenderActive = 0; // Interlocked

// OnPlaybackTick에서:
if (Interlocked.CompareExchange(ref _playbackRenderActive, 1, 0) == 0)
{
    _ = PlaybackRenderAsync(newTimeMs);  // 하나만 실행
}

// PlaybackRenderAsync의 finally에서:
Interlocked.Exchange(ref _playbackRenderActive, 0);  // 슬롯 해제
```
→ Mutex 경합은 해결되었으나, **여전히 재생이 안 됨**

### 원인 2: forward_threshold_ms가 너무 작음 (이것이 핵심)

```rust
// decoder.rs - Decoder 생성 시:
forward_threshold_ms: frame_duration_ms * 2,  // 30fps일 때 66ms
```

디코더의 3단계 판정 로직:
```rust
let gap_ms = timestamp_ms - self.last_timestamp_ms;

// 즉시 순차: gap <= 66ms → seek 없이 다음 프레임
let is_immediate = is_ahead && gap_ms <= frame_duration_ms * 2;
// Forward decode: gap <= threshold → seek 없이 PTS 확인하며 전진
let is_forward = is_ahead && !is_immediate && gap_ms <= self.forward_threshold_ms;
// 랜덤 접근: gap > threshold → FFmpeg seek 필요
let needs_seek = !is_immediate && !is_forward;
```

**문제 시나리오**:
1. 렌더 #1 (t=127341ms): 디코더 위치가 먼 곳 → **seek 발생** → 370ms 소요
2. 렌더 #2 (t=127711ms): 렌더 #1이 370ms 걸렸으므로 gap = 370ms
3. **370ms > 66ms (forward_threshold)** → 또 seek!
4. 렌더 #2도 370ms 소요 → gap 또 370ms → **무한 seek 루프**

매 프레임마다 seek → 키프레임에서 목표까지 디코딩 → 300-400ms → 다음 프레임도 seek → 반복

**Forward decode (seek 없음)는 ~5-10ms이지만, seek+decode는 300-400ms**
(키프레임 간격 2초 = 60프레임을 매번 새로 디코딩)

## 코드 파일

### 1. C# 재생 컨트롤: PreviewViewModel.cs

```csharp
// 30fps 타이머 → ThreadPool에서 실행
private void OnPlaybackTick(object? sender, ElapsedEventArgs e)
{
    var newTimeMs = _playbackStartTimeMs + _playbackClock.ElapsedMilliseconds;

    // 종료 체크
    if (newTimeMs >= maxEndTime) { Stop(); return; }

    // UI 시간 업데이트
    Dispatcher.UIThread.Post(() => { CurrentTimeMs = newTimeMs; });

    // 단일 렌더 슬롯: 이전 렌더 완료 시에만 새 렌더
    if (Interlocked.CompareExchange(ref _playbackRenderActive, 1, 0) == 0)
    {
        _ = PlaybackRenderAsync(newTimeMs);
    }
}

private async Task PlaybackRenderAsync(long timestampMs)
{
    try
    {
        byte[]? frameData = null;
        uint width = 0, height = 0;

        await Task.Run(() =>
        {
            using var frame = _projectService.RenderFrame(timestampMs);
            if (frame != null)
            {
                frameData = frame.Data.ToArray();
                width = frame.Width;
                height = frame.Height;
            }
        });

        if (frameData != null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => UpdateBitmap(frameData, width, height));
        }
    }
    finally
    {
        Interlocked.Exchange(ref _playbackRenderActive, 0);
    }
}
```

### 2. Rust FFI: renderer.rs (ffi)

```rust
#[no_mangle]
pub extern "C" fn renderer_render_frame(
    renderer: *mut c_void,
    timestamp_ms: i64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_data: *mut *mut u8,
    out_data_size: *mut usize,
) -> i32 {
    let renderer_mutex = &*(renderer as *const Mutex<Renderer>);

    let mut renderer_ref = match renderer_mutex.try_lock() {
        Ok(r) => r,
        Err(_) => {
            // Mutex busy → 프레임 스킵
            *out_width = 0;
            *out_height = 0;
            *out_data = std::ptr::null_mut();
            *out_data_size = 0;
            return ErrorCode::Success as i32;
        }
    };

    match renderer_ref.render_frame(timestamp_ms) {
        Ok(frame) => {
            *out_width = frame.width;
            *out_height = frame.height;
            *out_data_size = frame.data.len();
            let data_box = frame.data.into_boxed_slice();
            *out_data = Box::into_raw(data_box) as *mut u8;
            ErrorCode::Success as i32
        }
        Err(_) => {
            *out_width = 0;
            *out_height = 0;
            *out_data = std::ptr::null_mut();
            *out_data_size = 0;
            ErrorCode::Success as i32
        }
    }
}
```

### 3. Rust Renderer: renderer.rs (rendering)

```rust
pub fn render_frame(&mut self, timestamp_ms: i64) -> Result<RenderedFrame, String> {
    // 1. Timeline lock → 클립 조회
    let clips_to_render = {
        let timeline = self.timeline.lock()
            .map_err(|e| format!("Failed to lock timeline: {}", e))?;
        let mut clips = Vec::new();
        for track in &timeline.video_tracks {
            if let Some(clip) = track.get_clip_at_time(timestamp_ms) {
                if let Some(source_time_ms) = clip.timeline_to_source_time(timestamp_ms) {
                    clips.push((clip.clone(), source_time_ms));
                }
            }
        }
        clips
    }; // ← Timeline lock 해제

    if clips_to_render.is_empty() { return Ok(black_frame(timestamp_ms)); }

    let (clip, source_time_ms) = &clips_to_render[0];
    let file_path = clip.file_path.to_string_lossy().to_string();

    // 2. 프레임 캐시 조회
    if let Some(mut frame) = self.frame_cache.get(&file_path, *source_time_ms).cloned() {
        frame.timestamp_ms = timestamp_ms;
        return Ok(frame);
    }

    // 3. 디코딩 (여기가 느림!)
    let result = self.decode_clip_frame(clip, *source_time_ms);
    // ... DecodeResult 처리 ...
}

fn decode_clip_frame(&mut self, clip: &VideoClip, source_time_ms: i64) -> Result<DecodeResult, String> {
    let file_path = clip.file_path.to_string_lossy().to_string();

    // 디코더 캐시에서 가져오거나 새로 생성
    if !self.decoder_cache.contains_key(&file_path) {
        let decoder = Decoder::open(&clip.file_path)?;
        self.decoder_cache.insert(file_path.clone(), decoder);
    }

    let decoder = self.decoder_cache.get_mut(&file_path).ok_or("Decoder not found")?;
    decoder.decode_frame(source_time_ms)  // ← 이것이 300-400ms 걸림
}
```

### 4. Rust Decoder: decoder.rs (핵심)

```rust
pub fn decode_frame(&mut self, timestamp_ms: i64) -> Result<DecodeResult, String> {
    let frame_duration_ms = (1000.0 / self.fps).max(1.0) as i64;

    // 3단계 판정
    let is_ahead = self.state == DecoderState::Ready
        && timestamp_ms >= self.last_timestamp_ms;
    let gap_ms = timestamp_ms - self.last_timestamp_ms;

    let is_immediate = is_ahead && gap_ms <= frame_duration_ms * 2;  // ~66ms
    let is_forward = is_ahead && !is_immediate && gap_ms <= self.forward_threshold_ms;
    let needs_seek = !is_immediate && !is_forward;

    // ★ 여기가 문제: needs_seek가 매 프레임마다 true
    // forward_threshold_ms = 66ms인데, 실제 gap이 300-400ms
    if needs_seek {
        self.seek(timestamp_ms)?;  // FFmpeg seek → 키프레임까지 되돌아감
    }

    self.last_timestamp_ms = timestamp_ms;

    // PTS 확인 여부 결정
    let target_info = if is_immediate {
        None  // 순차: 다음 프레임 즉시 반환
    } else {
        // target_pts 계산 (time_base 기반)
        Some((target_pts, tolerance_pts))
    };

    // 패킷 읽기 + 디코딩 루프
    for (stream, packet) in self.input_ctx.packets() {
        if stream.index() != self.video_stream_index { continue; }
        self.decoder.send_packet(&packet)?;
        // receive_frame → PTS 확인 → 목표 도달 시 반환
    }

    // RGBA 변환
    let frame = self.convert_to_rgba(&raw_frame, timestamp_ms)?;
    Ok(DecodeResult::Frame(frame))
}
```

## 질문

1. **forward_threshold_ms를 5000ms (5초)로 올리는 것**이 올바른 해결책인가?
   - 장점: 재생 중 seek 대신 forward decode → 빠름
   - 단점: 스크럽 시 5초 이내 앞으로 이동하면 forward decode (seek보다 느릴 수 있음)

2. **더 나은 아키텍처**가 있는가?
   - 재생 전용 디코더를 별도로 유지 (forward_threshold = 무한대)?
   - `try_lock()` 대신 `lock()`을 쓰되 C# 쪽에서 호출 횟수 제한?
   - Decoder 내부에서 "재생 모드" 감지하여 자동으로 forward decode?

3. **C# 쪽 단일 렌더 슬롯 패턴**의 문제점은?
   - 스크럽의 `RenderFrameAsync`가 재생의 `PlaybackRenderAsync`와 Mutex를 공유
   - 스크럽 렌더가 아직 Mutex를 잡고 있을 때 재생 시작 → 첫 프레임 null

4. 재생 시작 전에 **디코더 워밍업** (첫 프레임을 동기적으로 렌더)이 필요한가?

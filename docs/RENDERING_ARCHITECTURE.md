# VortexCut 렌더링/플레이백 파이프라인 아키텍처

> **작성일**: 2026-02-11
> **상태**: Phase 1~4 구현 완료, Phase 5 (Decode-ahead) 설계 완료

---

## 1. 아키텍처 개요

### 1.1 이전 문제점 (주먹구구식)
- 매 프레임 FFmpeg 동기 디코딩 (캐시 없음)
- EOF 시 `Err("Failed to decode frame")` → 재생 중단
- 타이머 누적 오차 (`CurrentTimeMs += 33ms` 방식)
- 디코더 상태 관리 없음 (EOF/Error 구분 불가)
- 프레임 실패 = 검은 화면 또는 크래시

### 1.2 새 아키텍처 (구현 완료)
```
┌─────────────────────────────────────────────────────────────┐
│                    C# (Avalonia UI)                          │
│                                                              │
│  PreviewViewModel                                            │
│  ┌──────────────────────────────────────┐                   │
│  │ Stopwatch 플레이백 클럭              │                   │
│  │ _playbackStartTimeMs + Elapsed       │ (누적 오차 없음)  │
│  │     ↓                                │                   │
│  │ Timer(33ms) → ThreadPool             │                   │
│  │     ↓                                │                   │
│  │ Task.Run → RenderService.RenderFrame │                   │
│  │     ↓                                │                   │
│  │ WriteableBitmap A/B 더블버퍼         │                   │
│  └──────────────────────────────────────┘                   │
│           │ FFI (P/Invoke)                                   │
├───────────┼──────────────────────────────────────────────────┤
│           ▼                                                  │
│  Rust Engine                                                 │
│  ┌──────────────────────────────────────────────┐           │
│  │ Mutex<Renderer>                               │           │
│  │                                               │           │
│  │  ┌─────────────┐    ┌────────────────────┐   │           │
│  │  │ FrameCache  │◄───│ render_frame()     │   │           │
│  │  │ (LRU 60f)   │    │ 1. 캐시 조회       │   │           │
│  │  │ max 200MB   │    │ 2. 캐시 미스 → 디코딩│   │           │
│  │  └─────────────┘    │ 3. DecodeResult 처리│   │           │
│  │                     │ 4. fallback 보장    │   │           │
│  │                     └────────┬───────────┘   │           │
│  │                              │                │           │
│  │  ┌───────────────────────────▼────────────┐   │           │
│  │  │ Decoder (상태 머신)                     │   │           │
│  │  │                                         │   │           │
│  │  │ State: Ready ↔ EndOfStream → Error     │   │           │
│  │  │                                         │   │           │
│  │  │ decode_frame() → DecodeResult:          │   │           │
│  │  │   Frame(data)      = 정상 프레임        │   │           │
│  │  │   FrameSkipped     = 스킵 (계속 가능)   │   │           │
│  │  │   EndOfStream(last) = EOF + 마지막 프레임│   │           │
│  │  │   EndOfStreamEmpty  = EOF + 프레임 없음  │   │           │
│  │  │                                         │   │           │
│  │  │ seek() → 상태 자동 복구 (EOF→Ready)     │   │           │
│  │  └─────────────────────────────────────────┘   │           │
│  └──────────────────────────────────────────────┘           │
└─────────────────────────────────────────────────────────────┘
```

---

## 2. 핵심 컴포넌트

### 2.1 디코더 상태 머신 (`decoder.rs`)

```
          seek()
    ┌──────────────┐
    ▼              │
 [Ready] ──packets 소진──► [EndOfStream]
    │                          │
    │                     seek() → Ready
    │
    └──seek 2회 실패──► [Error]
```

| 상태 | 의미 | 동작 |
|------|------|------|
| `Ready` | 정상 디코딩 가능 | 패킷 읽기 + 프레임 반환 |
| `EndOfStream` | 파일 끝 도달 | 마지막 프레임 반환, seek()으로 복구 |
| `Error` | 복구 불가 에러 | 마지막 프레임 반환 (crash 방지) |

**DecodeResult 분기 (renderer.rs에서)**:
```
DecodeResult::Frame(f)       → 캐시 저장 + 반환
DecodeResult::FrameSkipped   → last_rendered_frame 반환 (또는 검정)
DecodeResult::EndOfStream(f) → 마지막 프레임 반환
DecodeResult::EndOfStreamEmpty → 검정 프레임
Err(e)                       → last_rendered_frame 반환 (로그만 출력)
```

**핵심 원칙**: `render_frame()`은 절대 `Err`을 C#에 전파하지 않음 → 재생 중단 없음.

### 2.2 LRU 프레임 캐시 (`renderer.rs`)

```
FrameCache {
    entries: VecDeque<CacheEntry>,  // (file_path, source_time_ms) → RGBA data
    max_entries: 60,                // ~2초 @ 30fps
    max_bytes: 200MB,               // 960x540x4 × 60 = ~120MB
}
```

**캐시 전략**:
- 순차 재생: 프레임이 순서대로 캐시에 쌓임 → decode-ahead와 결합 시 히트율 100%
- 스크럽: 이전 방문 프레임 즉시 반환
- 동일 프레임 반복 요청: 캐시 히트 (preview scrub 시 흔함)
- LRU eviction: 가장 오래된 프레임부터 제거

**FFI API**:
```
renderer_clear_cache(renderer)                          // 캐시 전체 클리어
renderer_get_cache_stats(renderer, &frames, &bytes)     // 통계 조회
```

### 2.3 플레이백 클럭 (`PreviewViewModel.cs`)

**이전**: `CurrentTimeMs += 33ms` (누적 오차)
**현재**: `Stopwatch` 기반 실시간 클럭

```csharp
// 재생 시작
_playbackStartTimeMs = CurrentTimeMs;
_playbackClock.Restart();

// 매 틱
newTimeMs = _playbackStartTimeMs + _playbackClock.ElapsedMilliseconds;
```

30fps 타이머 틱이 32ms 또는 34ms로 흔들려도, Stopwatch는 정확한 경과 시간을 보고 → 영상이 실시간 속도로 재생됨.

### 2.4 더블 버퍼링 (`PreviewViewModel.cs`)

```
_bitmapA ─┬─ 홀수 프레임에 쓰기
_bitmapB ─┘  짝수 프레임에 쓰기

PreviewImage = target;  // 매번 다른 객체 참조 → Avalonia 강제 갱신
```

Avalonia의 Image 바인딩은 같은 객체 참조의 PropertyChanged를 무시하므로, A/B 두 WriteableBitmap을 교대 사용.

---

## 3. 데이터 흐름

### 3.1 순차 재생 흐름

```
Timer(33ms tick)
  ↓
_playbackStartTimeMs + Stopwatch.Elapsed = newTimeMs
  ↓
Task.Run → RenderService.RenderFrame(newTimeMs)
  ↓ FFI
Mutex<Renderer>.try_lock()
  ├ busy → return null (프레임 스킵, C#에서 무시)
  └ ok →
    Timeline.lock() → clip + source_time 추출 → unlock
    FrameCache.get(file, source_time)
      ├ hit → 즉시 반환
      └ miss → Decoder.decode_frame(source_time)
        ├ Frame(f) → cache.put() + 반환
        ├ FrameSkipped → last_rendered 반환
        ├ EndOfStream(f) → 반환
        └ EndOfStreamEmpty → black_frame
  ↓
Marshal.Copy → byte[] → UI Thread Post → WriteableBitmap A/B swap
```

### 3.2 스크럽 (Seek) 흐름

```
Playhead 드래그 → RenderFrameAsync(timestampMs)
  ↓
Decoder.seek(timestamp) → flush() → state = Ready
  ↓
Decoder.decode_frame(timestamp) [PTS 매칭]
  seek → keyframe → 목표 PTS까지 디코딩 전진
  ↓
FrameCache.put() → 반환
```

### 3.3 EOF 흐름

```
재생 진행 → Decoder.decode_frame()
  ↓
packets iterator 소진 (파일 끝)
  ↓
state = EndOfStream
  ↓
return EndOfStream(last_decoded_frame.clone())
  ↓
Renderer: last_rendered_frame 유지 (화면 고정)
  ↓
C# Timer: maxEndTime 감지 → 재생 정지
```

---

## 4. 파일 구조 및 역할

| 파일 | 역할 |
|------|------|
| `rust-engine/src/ffmpeg/decoder.rs` | FFmpeg 디코더, 상태 머신, PTS 매칭, EOF 처리 |
| `rust-engine/src/rendering/renderer.rs` | Renderer + FrameCache (LRU), DecodeResult 처리 |
| `rust-engine/src/ffi/renderer.rs` | C# ↔ Rust FFI 인터페이스 |
| `VortexCut.Interop/NativeMethods.cs` | P/Invoke 선언 |
| `VortexCut.Interop/Services/RenderService.cs` | FFI 래핑 + SafeHandle |
| `VortexCut.UI/ViewModels/PreviewViewModel.cs` | 플레이백 클럭 + 더블 버퍼링 |

---

## 5. 향후 계획: Decode-Ahead (비동기 선행 디코딩)

**현재 상태**: 설계 완료, 미구현
**우선순위**: 높음 (순차 재생 성능 0ms 지연 달성)

### 5.1 설계

```rust
struct DecodeAhead {
    cmd_tx: mpsc::Sender<DecodeCommand>,  // 명령 채널
    thread: Option<JoinHandle<()>>,        // 백그라운드 스레드
}

enum DecodeCommand {
    Prefetch { file_path: String, start_ms: i64, count: usize },
    Seek { file_path: String, timestamp_ms: i64 },
    Stop,
}
```

### 5.2 동작 원리

1. `render_frame()` 호출 시 캐시 조회
2. 캐시 히트 → 즉시 반환 + **Prefetch 명령 전송** (다음 5프레임)
3. 백그라운드 스레드: Prefetch 수신 → 순차 디코딩 → 캐시에 저장
4. 다음 render_frame() → 캐시 히트 (decode-ahead 완료)

### 5.3 기대 효과

| 시나리오 | 현재 | Decode-ahead 후 |
|---------|------|----------------|
| 순차 재생 첫 프레임 | ~5ms | ~5ms |
| 순차 재생 2번째~ | ~3ms | 0ms (캐시 히트) |
| 스크럽 | ~10ms | ~10ms (seek 필수) |
| EOF 근처 | 에러 가능 | 안전 (상태 머신) |

---

## 6. 빌드 및 테스트

### 빌드
```bash
# Rust
cd rust-engine && cargo build --release
# DLL 복사
cp target/release/rust_engine.dll ../VortexCut.UI/runtimes/win-x64/native/

# C#
dotnet build VortexCut.UI -c Debug
```

### 테스트
```bash
# Rust 단위 테스트
cd rust-engine && cargo test

# C# 단위 테스트
dotnet test VortexCut.Tests
# 결과: 110 passed, 0 failed
```

### 기능 테스트 체크리스트
- [ ] 비디오 클립 추가 → Space 재생 → 끝까지 재생 → 에러 없이 정지
- [ ] Playhead 드래그 → 프레임 즉시 표시 (검은 화면 없음)
- [ ] 클립 끝 근처 재생 → 마지막 프레임 유지 (에러 없음)
- [ ] 같은 위치 반복 스크럽 → 캐시 히트 확인

---

**마지막 업데이트**: 2026-02-11

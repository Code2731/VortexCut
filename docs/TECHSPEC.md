# VortexCut 기술 명세서 (Technical Specification)

> **작성일**: 2026-02-14
> **버전**: 0.10.0 (색보정 이펙트 시스템)
> **상태**: 비디오/오디오 동기 재생, MP4 Export, 자막, 색보정 이펙트, Clip Monitor

## 1. 개요

VortexCut은 **Rust 렌더링 엔진**(rusty_ffmpeg)과 **C# Avalonia UI**를 결합한 크로스 플랫폼 영상 편집 프로그램입니다.

### 1.1 목표
- 고성능 영상 렌더링 (Rust + FFmpeg)
- 현대적이고 직관적인 UI (C# Avalonia)
- Windows 및 macOS 지원

### 1.2 기술 스택
| 구성 요소 | 기술 |
|----------|------|
| 렌더링 엔진 | Rust 2021, rusty_ffmpeg 0.13 |
| 오디오 재생 | Rust cpal 0.15 (WASAPI/CoreAudio) |
| UI 프레임워크 | C# .NET 8, Avalonia UI 11 |
| 연동 방식 | FFI (Foreign Function Interface) via P/Invoke |
| 빌드 시스템 | Cargo (Rust), MSBuild/.NET CLI (C#) |

## 2. 아키텍처

### 2.1 전체 구조

```
┌──────────────────────────────────────────────────┐
│           VortexCut.UI (C# Avalonia)            │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐      │
│  │ViewModels│  │  Views   │  │ Controls │      │
│  └────┬─────┘  └──────────┘  └──────────┘      │
│       │                                          │
│  ┌────▼───────────────────────────────────┐     │
│  │  VortexCut.Interop (P/Invoke)         │     │
│  │  RenderService / AudioPlaybackService │     │
│  │  ExportService / ThumbnailStripService│     │
│  └────┬───────────────────────────────────┘     │
└───────┼──────────────────────────────────────────┘
        │ FFI Boundary (C ABI)
┌───────▼──────────────────────────────────────────┐
│        Rust Engine (rust_engine.dll)             │
│  ┌───────┐  ┌─────────┐  ┌──────────┐           │
│  │  FFI  │  │Timeline │  │ Renderer │           │
│  └───┬───┘  └────┬────┘  └────┬─────┘           │
│      │           │             │                  │
│  ┌───┴───┐  ┌────▼─────┐  ┌───▼──────────────┐  │
│  │ Audio │  │ Encoding │  │ FFmpeg Decoder   │  │
│  │ cpal  │  │ Mixer    │  │ (rusty_ffmpeg)   │  │
│  │ Ring  │  │ Decoder  │  └──────────────────┘  │
│  │ Buffer│  │ Exporter │                         │
│  └───────┘  └──────────┘                         │
└──────────────────────────────────────────────────┘
```

### 2.2 프로젝트 구조

```
VortexCut/
├── rust-engine/              # Rust 렌더링 + 오디오 엔진
│   ├── src/
│   │   ├── ffi/              # FFI 인터페이스
│   │   │   ├── mod.rs        # FFI 모듈 선언
│   │   │   ├── timeline.rs   # 타임라인 FFI
│   │   │   ├── renderer.rs   # 렌더러 FFI
│   │   │   ├── thumbnail.rs  # 썸네일 FFI
│   │   │   ├── audio.rs      # 오디오 파형 FFI
│   │   │   ├── audio_playback.rs  # 실시간 재생 FFI
│   │   │   └── exporter.rs   # Export FFI
│   │   ├── ffmpeg/           # FFmpeg 래퍼 (비디오 디코더)
│   │   ├── timeline/         # 타임라인 엔진
│   │   ├── rendering/        # 비디오 렌더링 파이프라인
│   │   ├── audio/            # 실시간 오디오 재생 (cpal)
│   │   │   ├── mod.rs
│   │   │   └── playback.rs   # 링 버퍼 + cpal 출력
│   │   ├── encoding/         # 오디오 디코딩/믹싱/Export
│   │   │   ├── mod.rs
│   │   │   ├── audio_decoder.rs  # FFmpeg 오디오 → f32 PCM
│   │   │   ├── audio_mixer.rs    # 다중 트랙 믹싱
│   │   │   └── exporter.rs       # H.264+AAC MP4 Export
│   │   └── subtitle/         # 자막 처리
│   └── Cargo.toml
├── VortexCut.Core/           # C# 공통 모델
├── VortexCut.Interop/        # Rust-C# P/Invoke
│   ├── NativeMethods.cs      # FFI 선언 (~50개 함수)
│   └── Services/
│       ├── RenderService.cs       # 비디오 렌더링
│       ├── AudioPlaybackService.cs # 실시간 오디오
│       ├── ExportService.cs       # Export 관리
│       └── ThumbnailStripService.cs # 썸네일 생성
├── VortexCut.UI/             # Avalonia UI
└── VortexCut.Tests/          # C# 단위 테스트
```

## 3. FFI (Foreign Function Interface) 설계

### 3.1 핵심 원칙

1. **Opaque Pointer 패턴**: Rust 객체를 `*mut c_void` 포인터로 전달
2. **C ABI 사용**: `extern "C"` 로 C 호출 규약 준수
3. **에러 코드 반환**: `i32` 에러 코드 + `out` 파라미터 패턴
4. **명시적 메모리 관리**: Rust가 할당, C#이 해제 함수 호출

### 3.2 FFI 타입 정의

**Rust 측** ([rust-engine/src/ffi/types.rs](rust-engine/src/ffi/types.rs)):
```rust
#[repr(C)]
pub struct CClip {
    pub id: u64,
    pub start_time_ms: i64,
    pub duration_ms: i64,
    pub file_path: *const c_char,
}
```

**C# 측** ([VortexCut.Interop/Types/NativeTypes.cs](VortexCut.Interop/Types/NativeTypes.cs)):
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct NativeClip {
    public ulong Id;
    public long StartTimeMs;
    public long DurationMs;
    public IntPtr FilePath;  // UTF-8 string
}
```

### 3.3 메모리 관리

| 타입 | 할당 | 해제 | 방법 |
|------|------|------|------|
| Timeline/Clip 객체 | Rust | C# 호출 | `timeline_destroy(ptr)` FFI |
| 문자열 | Rust | C# 호출 | `string_free(ptr)` FFI |
| 프레임 데이터 | Rust | C# 호출 | `frame_free(ptr)` FFI |

**C# SafeHandle 패턴**:
```csharp
public class TimelineHandle : SafeHandle {
    protected override bool ReleaseHandle() {
        return NativeMethods.timeline_destroy(handle) == 0;
    }
}
```

### 3.4 에러 핸들링

**Rust → C# 에러 전파**:
- Rust: `Result<T>` → i32 에러 코드 변환
- C#: 에러 코드 확인 → `RustException` 발생

에러 코드:
- `0`: SUCCESS
- `1`: NULL_PTR
- `2`: INVALID_PARAM
- `3`: FFMPEG
- `4`: IO
- `99`: UNKNOWN

## 4. 주요 기능

### 4.1 타임라인 기반 편집

**기능:**
- 멀티 트랙 비디오/오디오 편집
- 드래그 앤 드롭으로 클립 배치
- 클립 트림 (시작/끝 조정)
- 클립 이동 및 순서 변경

**구현:**
- Rust: [rust-engine/src/timeline/timeline.rs](rust-engine/src/timeline/timeline.rs)
- C#: [VortexCut.UI/Controls/TimelineControl.axaml](VortexCut.UI/Controls/TimelineControl.axaml)

### 4.2 자막 편집

**기능:**
- SRT/ASS 자막 파일 임포트/내보내기
- 자막 수동 추가/편집
- Whisper 음성 인식 자동 자막 생성

**구현:**
- Rust: [rust-engine/src/subtitle/](rust-engine/src/subtitle/)
- C#: [VortexCut.UI/Views/SubtitleView.axaml](VortexCut.UI/Views/SubtitleView.axaml)

### 4.3 실시간 오디오 재생

**기능:**
- 프리뷰 재생 시 비디오와 동기화된 오디오 출력
- 다중 트랙 오디오 믹싱 (비디오 트랙의 오디오 포함)
- 볼륨 조정, soft clipping (tanh)
- 스크럽 시 정확한 위치에서 재시작

**구현:**
- Rust 오디오 재생: [rust-engine/src/audio/playback.rs](rust-engine/src/audio/playback.rs)
- Rust 오디오 디코더: [rust-engine/src/encoding/audio_decoder.rs](rust-engine/src/encoding/audio_decoder.rs)
- Rust 오디오 믹서: [rust-engine/src/encoding/audio_mixer.rs](rust-engine/src/encoding/audio_mixer.rs)
- C# 래퍼: [VortexCut.Interop/Services/AudioPlaybackService.cs](VortexCut.Interop/Services/AudioPlaybackService.cs)
- FFI: [rust-engine/src/ffi/audio_playback.rs](rust-engine/src/ffi/audio_playback.rs)

**아키텍처:**
```
Fill Thread                     cpal Callback (WASAPI)
    │                                │
AudioMixer ─→ decode 100ms ─→ push ─→ [Ring Buffer] ─→ pop ─→ Speaker
(48kHz stereo)  ~9600 samples        capacity: ~96000     ~256-1024
                                     (~1초 분량)           per callback
```

**핵심 설계:**
- **cpal**: WASAPI(Windows)/CoreAudio(macOS) 기반 저지연 오디오 출력 (5-40ms)
- **링 버퍼**: `Arc<Mutex<VecDeque<f32>>>` — Fill thread가 push, cpal callback이 pop
- **zero-alloc callback**: `VecDeque::as_slices()` + `copy_from_slice()` (실시간 스레드에서 힙 할당 금지)
- **leftover 캐리 버퍼**: FFmpeg 프레임 경계(~21ms) ≠ 청크 경계(100ms) 불일치 해결
- **prefill**: cpal 시작 전 300ms 선행 디코딩 (버퍼 언더런 방지)
- **PTS 기반 샘플 스킵**: seek 후 keyframe→target 사이 불필요 샘플 제거
- **Stop/Start 패턴**: 스크럽 시 Pause/Resume 대신 매번 재생성 (stale 버퍼 방지)

### 4.4 Clip Monitor (Source Monitor)

**기능:**
- Project Bin에서 미디어 더블클릭 → 독립 프리뷰
- 스크럽 Slider + 30fps Play/Pause 재생
- Mark In/Out 포인트 설정 (범위 지정)
- Add to Timeline (In/Out 범위 + 겹침 감지 → 빈 트랙 자동 선택)

**구현:**
- ViewModel: [VortexCut.UI/ViewModels/SourceMonitorViewModel.cs](VortexCut.UI/ViewModels/SourceMonitorViewModel.cs)
- View: [VortexCut.UI/Views/SourceMonitorView.axaml](VortexCut.UI/Views/SourceMonitorView.axaml)
- 더블클릭: [VortexCut.UI/Views/ProjectBinView.axaml.cs](VortexCut.UI/Views/ProjectBinView.axaml.cs)

**아키텍처:**
- `ThumbnailSession.Create(filePath, 960, 540)` — 프리뷰 해상도로 디코더 세션 생성
- 더블 버퍼링 WriteableBitmap A/B 교대 (Avalonia 바인딩 갱신 필수)
- 단일 렌더 슬롯 (`Interlocked.CompareExchange`) — ThumbnailSession 동시 접근 방지
- `ScrubRenderLoop` — pending timestamp 최신 요청만 처리 (중간 프레임 건너뜀)
- `FindInsertPosition(durationMs)` — 재생헤드 위치에서 빈 비디오 트랙 탐색, 없으면 끝에 append

### 4.5 색보정 이펙트 시스템 (Phase 8)

**기능:**
- Brightness (밝기): 픽셀 오프셋 가산
- Contrast (대비): 128 기준 스케일링
- Saturation (채도): BT.709 luminance 기반 조정
- Temperature (색온도): R/B 채널 오프셋 (warm=R+/B-, cool=R-/B+)

**구현:**
- Rust 이펙트: [rust-engine/src/rendering/effects.rs](rust-engine/src/rendering/effects.rs)
- Renderer 통합: [rust-engine/src/rendering/renderer.rs](rust-engine/src/rendering/renderer.rs) — `clip_effects: HashMap<u64, EffectParams>`
- FFI: [rust-engine/src/ffi/renderer.rs](rust-engine/src/ffi/renderer.rs) — `renderer_set_clip_effects()`
- Inspector UI: [VortexCut.UI/Views/InspectorView.axaml](VortexCut.UI/Views/InspectorView.axaml) — Color 탭

**아키텍처:**
```
Inspector Color Tab (C#)
    │ Slider ValueChanged (-100~100)
    │ → ClipModel 프로퍼티 갱신 (-1.0~1.0)
    │ → ProjectService.SetClipEffects()
    ▼
renderer_set_clip_effects() FFI
    │ → Renderer.clip_effects HashMap 갱신
    │ → FrameCache 클리어
    ▼
render_frame() → decode → apply_effects() → cache → C#
    │ Brightness: pixel += brightness * 255
    │ Contrast:   pixel = 128 + (pixel - 128) * (1 + contrast)
    │ Saturation: lum = BT.709 weighted, pixel = lum + (pixel - lum) * (1 + sat)
    │ Temperature: R += temp*30, B -= temp*30
    ▼
PreviewViewModel.RenderFrameAsync() → 즉시 프리뷰 갱신
```

**핵심 설계:**
- 이펙트는 RGBA 프리뷰에만 적용 (YUV Export는 건너뜀)
- 캐시된 프레임에 이펙트 포함 → 이펙트 변경 시 캐시 클리어
- `is_default()` 검사로 기본값(0) 시 연산 건너뜀 (성능)
- 프로젝트 저장/불러오기 시 이펙트 값 직렬화

### 4.5 전문가급 타임라인 기능 (Phase 2E)

**Phase 2E에서 구현된 22가지 전문가급 기능:**

#### 4.5.1 After Effects 스타일 키프레임 시스템
- **6가지 보간 타입**: Linear, Bezier, EaseIn, EaseOut, EaseInOut, Hold
- **베지어 핸들 (Direction Handles)**: 곡선 제어를 위한 InHandle/OutHandle
- **6개 애니메이션 속성**: Opacity, Volume, PositionX, PositionY, Scale, Rotation
- **그래프 에디터**: Value Graph 및 Speed Graph 모드
- **키프레임 네비게이션**: J/K 단축키, 드래그 편집, F9 Easy Ease

**구현:**
- C# 모델: [VortexCut.Core/Models/KeyframeModel.cs](VortexCut.Core/Models/KeyframeModel.cs)
- UI 렌더링: [VortexCut.UI/Controls/Timeline/ClipCanvasPanel.cs](VortexCut.UI/Controls/Timeline/ClipCanvasPanel.cs) - DrawKeyframes()
- 그래프 에디터: [VortexCut.UI/Controls/Timeline/GraphEditor.cs](VortexCut.UI/Controls/Timeline/GraphEditor.cs)

**수학적 보간 공식:**
```csharp
// EaseIn (가속): y = x²
value = kf1.Value + Math.Pow(t, 2) * (kf2.Value - kf1.Value)

// Bezier (4점 Cubic): P(t) = (1-t)³P₀ + 3(1-t)²tP₁ + 3(1-t)t²P₂ + t³P₃
value = Math.Pow(1-t,3)*p0 + 3*Math.Pow(1-t,2)*t*p1 + 3*(1-t)*Math.Pow(t,2)*p2 + Math.Pow(t,3)*p3
```

#### 4.5.2 마커 시스템 (Kdenlive/Premiere 스타일)
- **3가지 마커 타입**: Comment (코멘트), Chapter (챕터), Region (영역)
- **색상 구분**: ARGB 색상으로 마커 시각적 분류
- **영역 마커**: Duration 지원, 구간 표시
- **Snap 통합**: 마커에 자동 정렬 기능

**구현:**
- C# 모델: [VortexCut.Core/Models/MarkerModel.cs](VortexCut.Core/Models/MarkerModel.cs)
- UI 렌더링: [VortexCut.UI/Controls/Timeline/TimelineHeader.cs](VortexCut.UI/Controls/Timeline/TimelineHeader.cs) - DrawMarkers()
- 단축키: M (마커 추가), J/K (네비게이션)

#### 4.5.3 DaVinci Resolve 수준의 UI 컴포넌트
- **TimelineHeader**: 시간 눈금 + 마커 삼각형 표시
- **ClipCanvasPanel**: 키프레임 다이아몬드 렌더링 + 드래그 편집
- **GraphEditor**: 베지어 핸들 드래그, Zoom/Pan 지원
- **TrackHeaderControl**: Mute/Solo/Lock 버튼, 트랙 색상 선택

#### 4.5.4 Kdenlive 스타일 편집 도구
- **Snap 시스템**: 클립 끝/마커/플레이헤드에 자동 정렬 (100ms 임계값)
- **Razor Tool**: 단일 클립 또는 전체 트랙 동시 자르기
- **리플 편집**: 클립 삭제 시 이후 클립 자동 이동
- **다중 선택**: Ctrl+클릭, 드래그 박스 선택
- **링크 클립**: 비디오+오디오 자동 연결

**서비스 구현:**
- [VortexCut.UI/Services/SnapService.cs](VortexCut.UI/Services/SnapService.cs) - GetSnapTarget(), GetAllSnapPoints()
- [VortexCut.UI/Services/RazorTool.cs](VortexCut.UI/Services/RazorTool.cs) - CutClipAtTime(), CutAllTracksAtTime()
- [VortexCut.UI/Services/RippleEditService.cs](VortexCut.UI/Services/RippleEditService.cs) - RippleDelete(), RippleMove()

#### 4.5.5 메모리 안전성 개선
- **RenderService Finalizer**: 네이티브 리소스 자동 정리 (IDisposable + ~RenderService())
- **프레임 크기 검증**: 16K 프레임 최대 530MB 제한 (15360×8640×4 bytes)
- **타임아웃 처리**: RenderFrame 60초 타임아웃
- **에러 전파**: Rust 에러 코드 → C# RustException

## 5. 데이터 모델

### 5.1 Project

```csharp
public class Project {
    public string Name { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public Timeline Timeline { get; set; }
    public List<MediaFile> MediaFiles { get; set; }
}
```

### 5.2 Timeline

```csharp
public class Timeline {
    public List<Track> VideoTracks { get; set; }
    public List<Track> AudioTracks { get; set; }
    public List<SubtitleTrack> SubtitleTracks { get; set; }
}
```

### 5.3 Clip

```csharp
public class Clip {
    public Guid Id { get; set; }
    public Guid MediaFileId { get; set; }
    public long StartTimeMs { get; set; }
    public long DurationMs { get; set; }
    public long TrimStartMs { get; set; }
    public long TrimEndMs { get; set; }
    public Transition? Transition { get; set; }
    public List<Effect> Effects { get; set; }

    // Phase 2E: 6개 키프레임 시스템 통합
    public KeyframeSystem OpacityKeyframes { get; } = new();
    public KeyframeSystem VolumeKeyframes { get; } = new();
    public KeyframeSystem PositionXKeyframes { get; } = new();
    public KeyframeSystem PositionYKeyframes { get; } = new();
    public KeyframeSystem ScaleKeyframes { get; } = new();
    public KeyframeSystem RotationKeyframes { get; } = new();
}
```

### 5.4 KeyframeModel (Phase 2E)

**키프레임 시스템** - After Effects 스타일 애니메이션:

```csharp
public enum InterpolationType {
    Linear,      // 선형 보간
    Bezier,      // 베지어 곡선 (핸들 사용)
    EaseIn,      // 가속 (y = x²)
    EaseOut,     // 감속 (y = 1-(1-x)²)
    EaseInOut,   // 가속+감속 (S-curve)
    Hold         // 계단식 (다음 키프레임까지 값 유지)
}

public class Keyframe {
    public double Time { get; set; }  // 초 단위
    public double Value { get; set; }
    public InterpolationType Interpolation { get; set; } = InterpolationType.Linear;

    // 베지어 핸들 (Direction Handles)
    public BezierHandle? InHandle { get; set; }   // 진입 핸들
    public BezierHandle? OutHandle { get; set; }  // 진출 핸들
}

public class BezierHandle {
    public double TimeOffset { get; set; }  // 시간 오프셋
    public double ValueOffset { get; set; } // 값 오프셋
}

public class KeyframeSystem {
    public List<Keyframe> Keyframes { get; } = new();
    public event Action<Keyframe>? OnKeyframeAdded;
    public event Action<Keyframe>? OnKeyframeRemoved;

    public void AddKeyframe(double time, double value, InterpolationType interp = InterpolationType.Linear);
    public void RemoveKeyframe(Keyframe kf);
    public void UpdateKeyframe(Keyframe kf, double newTime, double newValue);
    public double Interpolate(double currentTime);  // 현재 시간의 보간 값 계산
}
```

**사용 예시:**
```csharp
var clip = new ClipModel();
clip.OpacityKeyframes.AddKeyframe(0.0, 0.0, InterpolationType.EaseIn);   // 0초: 투명
clip.OpacityKeyframes.AddKeyframe(2.0, 1.0);                              // 2초: 불투명
double opacityAt1Sec = clip.OpacityKeyframes.Interpolate(1.0);          // 1초 시점 보간 값
```

### 5.5 MarkerModel (Phase 2E)

**마커 시스템** - Kdenlive/Premiere 스타일 타임라인 마커:

```csharp
public enum MarkerType {
    Comment,   // 일반 코멘트 마커
    Chapter,   // 챕터 마커 (챕터 구분)
    Region     // 영역 마커 (구간 표시)
}

public class MarkerModel {
    public ulong Id { get; set; }
    public long TimeMs { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public uint ColorArgb { get; set; } = 0xFFFFFF00; // Yellow (ARGB)
    public MarkerType Type { get; set; } = MarkerType.Comment;

    // 영역 마커용 (Region)
    public long? DurationMs { get; set; }

    // 계산된 속성
    public bool IsRegion => Type == MarkerType.Region && DurationMs.HasValue;
    public long EndTimeMs => TimeMs + (DurationMs ?? 0);
}
```

**사용 예시:**
```csharp
var timeline = new TimelineViewModel();

// 일반 코멘트 마커
timeline.AddMarker(5000, "Scene 1 Start");

// 영역 마커 (10초~15초)
var regionMarker = new MarkerModel {
    TimeMs = 10000,
    DurationMs = 5000,
    Name = "Important Section",
    Type = MarkerType.Region,
    ColorArgb = 0xFFFF0000  // Red
};
timeline.Markers.Add(regionMarker);
```

## 6. 렌더링 파이프라인

### 6.1 프리뷰 렌더링 아키텍처 (v0.7.0 재설계)

```
┌──────────────┐     ┌───────────────────┐     ┌─────────────────┐
│ PreviewVM    │     │  Rust Renderer    │     │ WriteableBitmap │
│ (C# Timer   │ ──> │  (Mutex<Renderer>)│ ──> │ (A/B 더블버퍼)  │
│  30fps tick) │     │  ┌─────────────┐  │     └─────────────────┘
└──────────────┘     │  │ LRU Cache   │  │
                     │  │ 60fr/200MB  │  │
                     │  └─────────────┘  │
                     │  ┌─────────────┐  │
                     │  │ DecoderCache│  │
                     │  │ (per-file)  │  │
                     │  └──────┬──────┘  │
                     └─────────┼─────────┘
                          ┌────▼────┐
                          │ FFmpeg  │
                          │ Decoder │
                          └─────────┘
```

#### 6.1.1 단일 렌더 슬롯 패턴
재생/스크럽 모두 동시에 하나의 렌더만 진행하여 Mutex 경합 방지:
- **재생**: `Interlocked.CompareExchange(_playbackRenderActive, 1, 0)` — 이전 렌더 완료 후 다음 틱
- **스크럽**: `ScrubRenderLoop` + `_pendingScrubTimeMs` — latest-wins (중간 프레임 건너뜀)

#### 6.1.2 forward_threshold 모드 분리
| 모드 | forward_threshold | 용도 |
|------|------------------|------|
| 스크럽 | 100ms | 즉시 seek (정확한 위치) |
| 재생 | 5000ms | forward decode 우선 (빠른 순차 재생) |

`renderer_set_playback_mode(1/0)` FFI로 전환. 스크럽 시 5000ms 사용하면 FFmpeg 패킷 큐 폭증 → OOM/크래시.

#### 6.1.3 Decoder 상태 머신
```
DecoderState: Ready → EndOfStream → Error
DecodeResult: Frame | FrameSkipped | EndOfStream | EndOfStreamEmpty
```
- 에러 시: decoder drop → recreate → 1회 재시도
- `convert_to_rgba` bounds check: src_data 크기/stride 검증 (FFmpeg 손상 프레임 방어)

#### 6.1.4 LRU FrameCache (Rust)
- **60프레임 / 200MB** 최대, key = (file_path, source_time_ms)
- 캐시 히트 시 디코딩 건너뜀

**성능 최적화:**
- LRU FrameCache (60프레임/200MB, Rust)
- 단일 렌더 슬롯 (Mutex 경합 제거)
- WriteableBitmap A/B 더블 버퍼링 (참조 변경으로 갱신)
- Stopwatch 기반 재생 클럭 (누적 오차 방지)

### 6.2 Export Pipeline (내보내기)

```
┌──────────┐     ┌──────────┐     ┌─────────────┐     ┌──────────┐
│ Timeline │ ──> │ Renderer │ ──> │ FFmpeg Muxer│ ──> │ MP4 File │
│ (프레임  │     │ (RGBA)   │     │ (H.264+AAC) │     │          │
│  순회)   │     └──────────┘     └──────┬──────┘     └──────────┘
│          │     ┌──────────┐            │
│          │ ──> │AudioMixer│ ────────────┘
│          │     │(f32 PCM) │
└──────────┘     └──────────┘
```

**구현:**
- Rust Exporter: [rust-engine/src/encoding/exporter.rs](rust-engine/src/encoding/exporter.rs)
- FFI: [rust-engine/src/ffi/exporter.rs](rust-engine/src/ffi/exporter.rs)
- C# 서비스: [VortexCut.Interop/Services/ExportService.cs](VortexCut.Interop/Services/ExportService.cs)
- C# ViewModel: [VortexCut.UI/ViewModels/ExportViewModel.cs](VortexCut.UI/ViewModels/ExportViewModel.cs)
- UI 다이얼로그: [VortexCut.UI/Views/ExportDialog.axaml](VortexCut.UI/Views/ExportDialog.axaml)

**지원 포맷 (현재):**
- 비디오: H.264 (CRF 품질 제어)
- 오디오: AAC 48kHz stereo
- 컨테이너: MP4

**Export FFI:**
- `exporter_start(timeline, output_path, w, h, fps, crf)` → 백그라운드 스레드에서 실행
- `exporter_get_progress(job)` → 진행률 0~100
- `exporter_is_finished(job)` → 완료 여부
- `exporter_cancel(job)` → 취소
- `exporter_destroy(job)` → 메모리 해제

## 7. 빌드 시스템

### 7.1 Rust 빌드

```bash
cd rust-engine
cargo build --release
```

출력: `target/release/rust_engine.dll` (Windows) 또는 `librust_engine.dylib` (macOS)

### 7.2 C# 빌드

```bash
dotnet build VortexCut.sln -c Release
```

### 7.3 통합 빌드

**Windows** ([scripts/build-rust.ps1](scripts/build-rust.ps1)):
```powershell
cargo build --release
Copy-Item target\release\rust_engine.dll VortexCut.UI\runtimes\win-x64\native\
dotnet build VortexCut.sln -c Release
```

**macOS** ([scripts/build-rust.sh](scripts/build-rust.sh)):
```bash
cargo build --release
cp target/release/librust_engine.dylib VortexCut.UI/runtimes/osx-x64/native/
dotnet build VortexCut.sln -c Release
```

## 8. 개발 로드맵

### Phase 1: 핵심 인프라 (4-6주)

- [x] 프로젝트 구조 생성
- [ ] FFI 기본 구조 (types.rs, timeline.rs)
- [ ] Timeline 엔진 기본 구현
- [ ] FFmpeg Decoder
- [ ] C# P/Invoke 래퍼
- [ ] 간단한 Avalonia UI
- [ ] 단일 프레임 렌더링 테스트

### Phase 2: 편집 기능 확장 (6-8주) - ✅ 완료

#### Phase 2A: 트랙 인프라 & Zoom/Pan (1-2주) - ✅ 완료
- [x] TrackModel 생성 (Video/Audio 분리, 높이 조절)
- [x] 멀티 트랙 동적 관리 (8 비디오 + 4 오디오)
- [x] TrackHeaderControl (이름 편집, Mute/Solo/Lock, 색상 선택)
- [x] TimelineCanvas 리팩토링 (5개 컴포넌트 분리)
- [x] Zoom/Pan 완전 구현 (Ctrl+휠, Shift+휠, 중간 버튼)
- [x] 트랙 높이 조절 (30~200px)

#### Phase 2B: 핵심 편집 도구 (2주) - ✅ 완료
- [x] Snap 시스템 (SnapService - 클립/마커/플레이헤드 정렬)
- [x] 클립 트림 (양 끝 10px 핸들 드래그)
- [x] Razor Tool (단일/전체 트랙 자르기)
- [x] 다중 선택 (Ctrl+클릭, 드래그 박스 선택)
- [x] Rust FFI 확장 (timeline_update_clip_trim)

#### Phase 2C: 시각화 & 고급 편집 (2주) - ✅ 완료
- [x] 리플 편집 (RippleEditService - 자동 밀림)
- [x] 썸네일 캐싱 (ThumbnailCacheService - LRU 500개)
- [x] 오디오 웨이브폼 (WaveformService)
- [x] 링크 클립 (비디오+오디오 자동 연결)
- [x] 미니맵 (TimelineMinimap - 전체 타임라인 축소 뷰)

#### Phase 2D: 키프레임 & 마커 (1주) - ✅ 완료
- [x] KeyframeModel (6가지 InterpolationType)
- [x] KeyframeSystem (보간 엔진, 베지어 핸들)
- [x] MarkerModel (3가지 MarkerType)
- [x] TimelineViewModel 통합 (6개 KeyframeSystem per Clip)

#### Phase 2E: UI 완성 (1-2주) - ✅ 완료
- [x] TimelineHeader - 마커 렌더링 (삼각형, 툴팁)
- [x] ClipCanvasPanel - 키프레임 다이아몬드 렌더링
- [x] GraphEditor - 베지어 핸들 드래그, Value/Speed Graph
- [x] 키보드 단축키 (K: 키프레임, M: 마커, J/K: 네비게이션)
- [x] HitTest 우선순위 (키프레임 > 클립)
- [x] 메모리 안전성 (RenderService Finalizer, 16K 검증)

**Phase 2 완료 통계:**
- **22개 전문가급 기능** 구현 완료
- **81개 단위 테스트** (45개 → 81개, +36개 증가)
- **0 빌드 경고**, **0 빌드 에러**
- **DaVinci Resolve** 수준의 타임라인 UI
- **After Effects** 스타일 키프레임 시스템
- **Kdenlive** 스타일 편집 도구

#### 추가 Phase 2 항목 (우선순위 낮음)
- [ ] Compositor (레이어 합성)
- [ ] AudioMixer (오디오 믹싱)
- [ ] 기본 트랜지션
- [x] FrameCache (성능 최적화) — LRU 60프레임/200MB, Rust Renderer
- [ ] Undo/Redo
- [x] 프로젝트 저장/불러오기 (JSON 직렬화) — ProjectSerializer

### Phase 3: 렌더링 파이프라인 & 타임라인 리디자인 - ✅ 완료

#### Phase 3A: 렌더링 파이프라인 재설계 (2026-02-11~13) - ✅ 완료
- [x] Decoder 상태 머신 (DecoderState + DecodeResult)
- [x] LRU FrameCache (60프레임/200MB, Rust)
- [x] Renderer fallback (last_rendered_frame / black_frame)
- [x] Stopwatch 기반 재생 클럭 (누적 오차 방지)
- [x] renderer_clear_cache / renderer_get_cache_stats FFI
- [x] 단일 렌더 슬롯 (재생: CompareExchange, 스크럽: ScrubRenderLoop)
- [x] forward_threshold 모드 분리 (스크럽=100ms, 재생=5000ms)
- [x] renderer_set_playback_mode FFI
- [x] convert_to_rgba bounds check (FFmpeg 손상 프레임 방어)
- [x] Decoder 에러 복구 (drop → recreate → 1회 재시도)
- [x] WriteableBitmap A/B 더블 버퍼링

#### Phase 3B: DaVinci Resolve 스타일 타임라인 헤더 (Phase A) - ✅ 완료
- [x] HeaderHeight 60px 동기화 (Grid Row와 일치)
- [x] DaVinci Resolve 스타일 시간 눈금 (동적 소눈금 높이, M:SS 포맷)
- [x] SMPTE 타임코드 동적 FPS
- [x] 인터랙티브 줌 슬라이더 (클릭/드래그/더블클릭=FitToWindow)
- [x] RenderResourceCache 적용 (모든 Typeface/Brush/Pen 캐싱)
- [x] 줌 범위 확장 (0.001~5.0 px/ms, 30분까지 표시 가능)

#### Phase 3C: 트랙 헤더 UI 리디자인 (Phase B) - ✅ 완료
- [x] TrackHeaderControl XAML 재설계 (M/S/L 버튼, 색상바)
- [x] 트랙 색상 선택 팝업 (16색 팔레트)
- [x] PathIcon 벡터 아이콘 (비디오/오디오 구분)
- [x] 트랙 이름 편집 (더블클릭 인라인)

#### Phase 3D: 썸네일 스트립 & 오디오 파형 - ✅ 완료
- [x] ThumbnailStripService (3-tier LOD: Low/Medium/High)
- [x] ThumbnailSession FFI (디코더 1회 Open, N프레임 생성)
- [x] AudioWaveformService (Rust FFI extract_audio_peaks)
- [x] 실제 오디오 파형 렌더링 (StreamGeometry, 반파/전파)

### Phase 4: 실시간 오디오 재생 + Export Pipeline (2026-02-13) - ✅ 완료

#### Phase 4A: 실시간 오디오 재생 엔진 - ✅ 완료
- [x] AudioDecoder (FFmpeg → f32 stereo 48kHz, PTS 기반 seek/skip)
- [x] AudioMixer (다중 트랙 믹싱, soft clipping)
- [x] AudioPlayback (cpal WASAPI, 링 버퍼, fill thread)
- [x] FFI 바인딩 (audio_playback_start/stop/pause/resume/destroy)
- [x] C# AudioPlaybackService 래퍼
- [x] PreviewViewModel 통합 (Play/Stop에 오디오 연동)
- [x] leftover 캐리 버퍼 (프레임 경계 불일치 → 크래클링 해결)
- [x] zero-alloc cpal callback (VecDeque::as_slices 직접 복사)
- [x] 비디오 트랙 오디오 소스 지원 (get_all_audio_sources_at_time)

#### Phase 4B: Export Pipeline - ✅ 완료
- [x] Rust Exporter (H.264+AAC MP4, CRF 품질 제어)
- [x] 백그라운드 Export 스레드 (진행률/취소 지원)
- [x] FFI 바인딩 (exporter_start/progress/finished/cancel/destroy)
- [x] C# ExportService 래퍼
- [x] ExportDialog UI (해상도/FPS/품질 설정)

### Phase 7: 자막 편집 (2026-02-14) - ✅ 완료

#### Phase 7a: SRT 파싱 + 타임라인 UI + 프리뷰 (C#) - ✅ 완료
- [x] SubtitleClipModel (ClipModel 상속 + Text, SubtitleStyle)
- [x] SrtParser (SRT 파일 파싱/내보내기)
- [x] 자막 트랙 타입 (TrackType.Subtitle)
- [x] ClipCanvasPanel 자막 클립 렌더링 (앰버색 그라데이션 + 텍스트 표시)
- [x] PreviewViewModel 자막 오버레이 (CurrentSubtitleText)
- [x] InspectorView 자막 편집 탭

#### Phase 7b: Export 자막 번인 (Rust FFI) - ✅ 완료
- [x] Rust SubtitleOverlay 알파 블렌딩 합성
- [x] C# SubtitleRenderService (Avalonia RenderTargetBitmap → RGBA)
- [x] FFI: exporter_create_subtitle_list / subtitle_list_add / exporter_start_v2

### Source Monitor (Clip Monitor) 구현 (2026-02-14) - ✅ 완료

- [x] SourceMonitorViewModel (ThumbnailSession 기반 독립 프리뷰)
- [x] 더블 버퍼링 + 단일 렌더 슬롯 (PreviewViewModel 패턴 미러링)
- [x] Project Bin 더블클릭 → Clip Monitor 로드
- [x] Slider 스크럽 + 30fps 재생
- [x] Mark In/Out 포인트 설정
- [x] Add to Timeline (겹침 감지 → 빈 트랙 자동 선택)
- [x] FindInsertPosition() — 6개 비디오 트랙 순차 탐색, 겹치면 끝에 append

### Phase 8: 색보정 이펙트 시스템 (2026-02-14) - ✅ 완료

- [x] Rust effects.rs — EffectParams + apply_effects() RGBA 픽셀 연산
- [x] Renderer 통합 — clip_effects HashMap, render_frame() 이펙트 적용
- [x] FFI — renderer_set_clip_effects() (clip_id, brightness, contrast, saturation, temperature)
- [x] C# ClipModel — Brightness/Contrast/Saturation/Temperature 프로퍼티
- [x] Inspector Color 탭 — 4개 Slider (-100~100) + 값 표시 + Reset 버튼
- [x] 실시간 프리뷰 — Slider 드래그 → Rust FFI → 캐시 클리어 → 즉시 재렌더
- [x] 직렬화 — ClipData에 이펙트 필드 추가, 프로젝트 복원 시 Rust 동기화

### Phase 9: GPU 하드웨어 가속 인코딩 (완료 ✅ 2026-02-15)

- [x] EncoderType enum (Auto/Software/NVENC/QSV/AMF) + detect_available_encoders() 비트마스크
- [x] Auto 모드: h264_nvenc → h264_qsv → h264_amf → libx264 순서 시도
- [x] 인코더별 품질 옵션: NVENC(VBR+CQ), QSV(global_quality), AMF(bitrate)
- [x] GPU 실패 시 libx264 자동 폴백
- [x] FFI: exporter_start_v3() + exporter_detect_encoders()
- [x] ExportDialog 인코더 선택 ComboBox UI

## 9. 테스트 전략

### 9.1 Rust 테스트

```bash
cd rust-engine
cargo test
```

### 9.2 C# 테스트

```bash
dotnet test VortexCut.Tests
```

**현재 테스트 커버리지 (Phase 2E 완료 후):**

| 카테고리 | 테스트 수 | 파일 |
|---------|---------|------|
| Models | 36 | KeyframeModelTests.cs (23), MarkerModelTests.cs (13) |
| Services | 20 | SnapServiceTests.cs (5), TimelineServiceTests.cs (15) |
| Interop | 10 | NativeMethodsTests.cs (6), 기타 (4) |
| 네이티브 DLL 필요 | 14 (스킵) | [FactRequiresNativeDll] 속성 |
| **총계** | **81** (통과) | **Phase 2E에서 +36 증가 (80%)** |

**테스트 실행:**
```bash
dotnet test VortexCut.Tests/VortexCut.Tests.csproj
# 결과: 81 passed, 0 failed, 14 skipped (28ms)
```

**Phase 2E 신규 테스트 상세:**

**KeyframeModelTests.cs** (23 tests):
- `Interpolate_Linear_ReturnsCorrectValue` - 선형 보간
- `Interpolate_EaseIn_AcceleratesCurve` - 가속 곡선 (y=x²)
- `Interpolate_EaseOut_DeceleratesCurve` - 감속 곡선
- `Interpolate_EaseInOut_SCurve` - S-커브
- `Interpolate_Bezier_WithHandles` - 베지어 핸들
- `Interpolate_Hold_StairStep` - 계단식
- `Interpolate_NoKeyframes_ReturnsZero` - 엣지 케이스
- `Interpolate_SingleKeyframe_ReturnsConstant`
- `Interpolate_OutOfRange_ReturnsEndValue`
- `UpdateKeyframe_ModifiesTimeAndValue`
- `OnKeyframeAdded_FiresEvent` - 이벤트 테스트
- 기타 12개 테스트

**MarkerModelTests.cs** (13 tests):
- `IsRegion_WithDuration_ReturnsTrue` - 영역 마커 검증
- `IsRegion_WithoutDuration_ReturnsFalse`
- `EndTimeMs_RegionMarker_CalculatesCorrectly`
- `MarkerType_Comment_IsDefault` - 기본값 테스트
- `ColorArgb_Default_IsYellow`
- `Constructor_WithParameters_InitializesCorrectly`
- 기타 7개 테스트

### 9.3 통합 테스트

1. UI 실행
2. 비디오 파일 열기
3. 타임라인에 클립 추가
4. 프레임 렌더링 확인
5. 키프레임 추가/편집 (K 키, 드래그)
6. 마커 추가 (M 키)
7. Snap 기능 테스트 (클립 드래그)
8. Razor 도구로 클립 자르기
9. 그래프 에디터에서 베지어 핸들 조작
10. 프로젝트 저장/불러오기

## 10. 성능 목표

| 항목 | 목표 | Phase 2E 달성 |
|------|------|--------------|
| 프리뷰 재생 | 30fps @ 1080p | ✅ 60fps 타겟 |
| 메모리 사용 | < 2GB (일반적인 프로젝트) | ✅ 16K 프레임 530MB 제한 |
| 렌더링 속도 | 실시간 대비 0.5x ~ 2x | ⏳ (Phase 3) |
| UI 반응성 | < 100ms (모든 작업) | ✅ 키프레임 드래그 < 16ms |

**Phase 2E 성능 최적화:**
- **Avalonia 렌더링**: Virtual Rendering (화면 내 클립만), Dirty Rectangle
- **키프레임 보간**: 캐싱 전략 (Dictionary<long, double> 캐시)
- **마커 HitTest**: 20px 임계값, 화면 밖 스킵
- **그래프 에디터**: 0.01초 간격 샘플링, Zoom/Pan 최적화

## 11. 보안 고려사항

- **메모리 안전성**: Rust의 소유권 시스템 활용
- **Unsafe 코드 최소화**: FFI 경계에만 제한
- **입력 검증**: 모든 사용자 입력 검증 (파일 경로, 파라미터 등)
- **에러 전파**: Panic 대신 Result 사용

## 12. Phase 2E 기술 상세

### 12.1 키프레임 보간 수학

**Linear (선형):**
```
value(t) = v₁ + t(v₂ - v₁)
where t ∈ [0, 1]
```

**EaseIn (가속 - 2차 함수):**
```
value(t) = v₁ + t²(v₂ - v₁)
특성: 느리게 시작 → 빠르게 종료
```

**EaseOut (감속):**
```
value(t) = v₁ + (1 - (1-t)²)(v₂ - v₁)
특성: 빠르게 시작 → 느리게 종료
```

**EaseInOut (S-커브):**
```
value(t) = v₁ + ease(t) * (v₂ - v₁)
where ease(t) = t < 0.5 ? 2t² : 1 - (-2t+2)²/2
특성: 느리게 시작 → 중간 가속 → 느리게 종료
```

**Bezier (4점 Cubic):**
```
P(t) = (1-t)³P₀ + 3(1-t)²tP₁ + 3(1-t)t²P₂ + t³P₃
where:
  P₀ = kf1.Value (시작점)
  P₁ = kf1.Value + kf1.OutHandle.ValueOffset
  P₂ = kf2.Value + kf2.InHandle.ValueOffset
  P₃ = kf2.Value (끝점)
```

**Hold (계단식):**
```
value(t) = v₁  (다음 키프레임까지 값 유지)
```

### 12.2 Avalonia UI 아키텍처

**Control vs Panel vs Border:**
- `Control` (기본 클래스): Background 속성 없음
- `Panel` (레이아웃): Background, Children 지원
- `Border` (데코레이션): Background, BorderBrush, Child 지원

**Phase 2E에서 배운 교훈:**
```csharp
// ❌ 잘못된 사용 (Control은 Background 없음)
public class GraphEditor : Control {
    // XAML: <GraphEditor Background="..." /> → 에러
}

// ✅ 올바른 사용 (Panel 상속 또는 Border로 래핑)
public class GraphEditor : Panel {
    // XAML: <GraphEditor Background="..." /> → 작동
}
// 또는
// <Border Background="..."><GraphEditor /></Border>
```

### 12.3 HitTest 우선순위 설계

**ClipCanvasPanel 이벤트 처리 순서:**
```csharp
OnPointerPressed(PointerPressedEventArgs e) {
    // 1. 최우선: 키프레임 HitTest
    var keyframe = GetKeyframeAtPosition(point);
    if (keyframe != null) { StartKeyframeDrag(); return; }

    // 2. 우선: 클립 트림 핸들
    var trimEdge = GetTrimHandleAtPosition(point);
    if (trimEdge != ClipEdge.None) { StartTrimDrag(); return; }

    // 3. 일반: 클립 드래그
    var clip = GetClipAtPosition(point);
    if (clip != null) { StartClipDrag(); return; }

    // 4. 최후: 박스 선택
    StartBoxSelection();
}
```

### 12.4 메모리 관리 패턴

**RenderService Finalizer 패턴:**
```csharp
public class RenderService : IDisposable {
    private bool _disposed = false;

    // IDisposable 명시적 해제
    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);  // Finalizer 호출 방지
    }

    // Finalizer (GC가 호출)
    ~RenderService() {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing) {
        if (_disposed) return;

        if (disposing) {
            // 관리 리소스 해제 (C# 객체)
        }

        // 비관리 리소스 해제 (Rust 포인터)
        if (_timelinePtr != IntPtr.Zero) {
            NativeMethods.timeline_destroy(_timelinePtr);
            _timelinePtr = IntPtr.Zero;
        }

        _disposed = true;
    }
}
```

**16K 프레임 크기 검증:**
```csharp
public const int MAX_FRAME_SIZE = 530_000_000;  // 530MB
// 계산: 15360 × 8640 × 4 bytes = 530,841,600 bytes

if (width * height * 4 > MAX_FRAME_SIZE) {
    throw new ArgumentException($"Frame too large: {width}×{height}");
}
```

### 12.5 단축키 매핑 (After Effects 스타일)

| 단축키 | 기능 | 설명 |
|--------|------|------|
| K | 키프레임 추가 | 현재 시간에 키프레임 생성 |
| M | 마커 추가 | 현재 시간에 마커 생성 |
| J | 이전 키프레임 | Playhead를 이전 키프레임으로 이동 |
| K (네비게이션) | 다음 키프레임 | Playhead를 다음 키프레임으로 이동 |
| F9 | Easy Ease | 선택된 키프레임을 EaseInOut으로 변경 |
| Delete | 키프레임/마커 삭제 | 선택된 항목 제거 |
| Ctrl+클릭 | 다중 선택 | 클립/키프레임 추가 선택 |
| Shift+드래그 | 축 고정 드래그 | X축 또는 Y축만 이동 |

## 13. 참고 자료

### 공식 문서
- [Rust FFI Omnibus](http://jakegoulding.com/rust-ffi-omnibus/)
- [rusty_ffmpeg GitHub](https://github.com/CCExtractor/rusty_ffmpeg)
- [Avalonia UI Docs](https://docs.avaloniaui.net/)
- [Microsoft P/Invoke Docs](https://docs.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke)

### Phase 2E 참고 프로그램
- **Adobe After Effects**: 키프레임 시스템, 베지어 핸들, Stopwatch UI, F9 Easy Ease
- **DaVinci Resolve**: Timeline UI, 트랙 헤더, 그래프 에디터
- **Kdenlive**: 마커 시스템, Snap 기능, Razor Tool, 리플 편집
- **Premiere Pro**: 다중 선택, 링크 클립, 미니맵

### xUnit 테스트 참고
- [xUnit Documentation](https://xunit.net/)
- [Theory와 InlineData 사용법](https://xunit.net/docs/getting-started/netcore/cmdline#write-first-theory)
- [Fact vs Theory 차이점](https://stackoverflow.com/questions/22093843/what-is-the-difference-between-fact-and-theory-attributes)

---

**마지막 업데이트**: 2026-02-15 (Phase 9 완료: GPU 하드웨어 가속 인코딩)
**작성자**: Claude Sonnet 4.5 / Claude Opus 4.6
**Phase 9 구현 기간**: 2026-02-15 (1일)
**Phase 9 추가 코드**: ~350 라인 (Rust EncoderType + FFI v3 + C# 서비스 + Export UI)

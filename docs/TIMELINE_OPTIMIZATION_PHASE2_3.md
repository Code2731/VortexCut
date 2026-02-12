# VortexCut 타임라인 렌더링 최적화 - Phase 2 & 3 구현 설계서

> **작성일**: 2026-02-11
> **Phase 1 상태**: 완료 (RenderResourceCache.cs + ClipCanvasPanel 전체 브러시/펜 캐싱)
> **Phase 2 상태**: ✅ 완료 (ThumbnailStripService + 점진적 썸네일 렌더링)
> **Phase 3 상태**: ✅ 완료 (글로우 10fps 제한 + 스냅샷 변경 감지 + LOD 자동 조절)

---

## Phase 1 완료 요약

### 생성 파일
- `VortexCut.UI/Controls/Timeline/RenderResourceCache.cs`
  - 정적 불변 브러시 17개 (BackgroundBrush, WhiteBrush, LinkBrush 등)
  - 정적 불변 펜 24개 (TrackBorderPen, PlayheadBodyPen, SnapMainPen 등)
  - 정적 타입페이스 3개 (SegoeUI, SegoeUIBold, Consolas)
  - 정적 그라디언트 4개 (PlayheadHeadGradient, TooltipBgGradient 등)
  - 동적 풀 3개: `GetSolidBrush(Color)`, `GetVerticalGradient(Color, Color)`, `GetPen(Color, double)`
  - 모든 브러시 `.ToImmutable()` 적용

### 수정 파일
- `VortexCut.UI/Controls/Timeline/ClipCanvasPanel.cs`
  - 10개 렌더링 메서드 전체 캐시 적용 완료
  - DrawTrackBackgrounds, DrawClip(Minimal/Medium/Full), DrawAudioWaveform, DrawTransitionOverlay
  - DrawKeyframes, DrawKeyframeDiamond, DrawLinkedClipConnections
  - DrawPlayhead, DrawClipTooltip, DrawPerformanceInfo, DrawSnapGuideline

### 효과
- 프레임당 80-120개 단기 힙 할당 → 5-10개 (FormattedText만 남음)
- GC Gen0 빈도 대폭 감소

---

## Phase 2: 썸네일 스트립 시스템

### 2.1 목표
비디오 클립 내부에 실제 프레임 썸네일을 표시하여 Premiere Pro/DaVinci Resolve 수준의 클립 시각화 제공.

### 2.2 기존 인프라 (변경 불필요)

#### Rust FFI (이미 구현됨)
```csharp
// VortexCut.Interop/NativeMethods.cs:188-197
[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
public static extern int generate_video_thumbnail(
    IntPtr filePath, long timestampMs,
    uint thumbWidth, uint thumbHeight,
    out uint outWidth, out uint outHeight,
    out IntPtr outData, out nuint outDataSize);
```

#### C# 래핑 서비스 (이미 구현됨)
```csharp
// VortexCut.Interop/Services/RenderService.cs:232-277
public static RenderedFrame GenerateThumbnail(
    string filePath, long timestampMs = 0,
    uint thumbWidth = 160, uint thumbHeight = 90)
```
- UTF-8 마샬링으로 한글 경로 지원
- `RenderedFrame` 반환 (Width, Height, Data span, IDisposable)

### 2.3 새 파일: `VortexCut.UI/Services/ThumbnailStripService.cs`

#### 데이터 구조

```csharp
namespace VortexCut.UI.Services;

/// <summary>
/// 줌 레벨별 썸네일 해상도 티어
/// </summary>
public enum ThumbnailTier
{
    Low,    // 80×45  (극 축소, _pixelsPerMs < 0.05)
    Medium, // 120×68 (보통 줌, 0.05 ~ 0.3)
    High    // 160×90 (확대, > 0.3)
}

/// <summary>
/// 캐시된 개별 썸네일
/// </summary>
public class CachedThumbnail
{
    public long SourceTimeMs { get; set; }
    public WriteableBitmap Bitmap { get; set; } = null!;
}

/// <summary>
/// 클립별 썸네일 스트립 (점진적으로 채워지는 썸네일 목록)
/// </summary>
public class ThumbnailStrip
{
    public string FilePath { get; set; } = "";
    public ThumbnailTier Tier { get; set; }
    public long IntervalMs { get; set; }
    public long DurationMs { get; set; }
    public List<CachedThumbnail> Thumbnails { get; } = new();
    public bool IsComplete { get; set; }
    public bool IsGenerating { get; set; }
    public long MemoryBytes { get; set; }
}

/// <summary>
/// 캐시 키: (파일 경로, 티어)
/// </summary>
public record ThumbnailStripKey(string FilePath, ThumbnailTier Tier);
```

#### 서비스 클래스 설계

```csharp
/// <summary>
/// 클립별 썸네일 스트립 생성/캐시 서비스
/// - LRU 캐시 (256MB 예산, 최대 200개 스트립)
/// - 백그라운드 비동기 생성 (SemaphoreSlim 동시 2개 제한)
/// - 점진적 표시 (생성 완료된 썸네일부터 즉시 렌더링)
/// </summary>
public class ThumbnailStripService : IDisposable
{
    // === 캐시 ===
    private readonly Dictionary<ThumbnailStripKey, ThumbnailStrip> _cache = new();
    private readonly LinkedList<ThumbnailStripKey> _lruOrder = new();
    private long _currentMemoryBytes = 0;
    private const long MaxMemoryBytes = 256 * 1024 * 1024; // 256MB
    private const int MaxStrips = 200;

    // === 동시성 ===
    private readonly SemaphoreSlim _generationSemaphore = new(2); // FFmpeg 디코더 동시 2개
    private CancellationTokenSource _cts = new();

    // === UI 갱신 콜백 ===
    /// <summary>
    /// 썸네일 생성 완료 시 호출 → ClipCanvasPanel.InvalidateVisual()
    /// </summary>
    public Action? OnThumbnailReady { get; set; }

    // === 공개 API ===

    /// <summary>
    /// 썸네일 스트립 조회 또는 생성 요청
    /// 캐시 히트: 즉시 반환
    /// 캐시 미스: 빈 스트립 반환 + 배경 생성 시작
    /// </summary>
    public ThumbnailStrip? GetOrRequestStrip(string filePath, long durationMs, ThumbnailTier tier)
    {
        var key = new ThumbnailStripKey(filePath, tier);

        // 캐시 히트
        if (_cache.TryGetValue(key, out var strip))
        {
            // LRU 갱신: 뒤로 이동
            _lruOrder.Remove(key);
            _lruOrder.AddLast(key);
            return strip;
        }

        // 캐시 미스: 새 스트립 생성
        var (thumbWidth, thumbHeight, intervalMs) = GetTierParams(tier, durationMs);

        strip = new ThumbnailStrip
        {
            FilePath = filePath,
            Tier = tier,
            IntervalMs = intervalMs,
            DurationMs = durationMs,
            IsGenerating = true,
        };

        // LRU 관리 + 메모리 예산 확인
        EvictIfNeeded(EstimateStripMemory(thumbWidth, thumbHeight, durationMs, intervalMs));

        _cache[key] = strip;
        _lruOrder.AddLast(key);

        // 백그라운드 생성 시작
        _ = GenerateStripAsync(key, strip, thumbWidth, thumbHeight, _cts.Token);

        return strip;
    }

    /// <summary>
    /// 줌 레벨에서 적절한 ThumbnailTier 계산
    /// </summary>
    public static ThumbnailTier GetTierForZoom(double pixelsPerMs)
    {
        if (pixelsPerMs < 0.05) return ThumbnailTier.Low;
        if (pixelsPerMs < 0.3) return ThumbnailTier.Medium;
        return ThumbnailTier.High;
    }

    /// <summary>
    /// 특정 파일의 캐시 무효화 (파일 재인코딩 시)
    /// </summary>
    public void InvalidateFile(string filePath)
    {
        var keysToRemove = _cache.Keys
            .Where(k => k.FilePath == filePath)
            .ToList();

        foreach (var key in keysToRemove)
        {
            RemoveStrip(key);
        }
    }

    /// <summary>
    /// 전체 캐시 클리어 (프로젝트 전환 시)
    /// </summary>
    public void ClearAll()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();

        foreach (var strip in _cache.Values)
        {
            // WriteableBitmap은 Dispose 불필요 (GC 수거)
            strip.Thumbnails.Clear();
        }
        _cache.Clear();
        _lruOrder.Clear();
        _currentMemoryBytes = 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _generationSemaphore.Dispose();
        ClearAll();
    }

    // === 내부 구현 ===

    /// <summary>
    /// 티어별 파라미터 계산
    /// </summary>
    private static (uint thumbWidth, uint thumbHeight, long intervalMs) GetTierParams(
        ThumbnailTier tier, long durationMs)
    {
        return tier switch
        {
            ThumbnailTier.Low => (80, 45, Math.Max(5000, durationMs / 10)),
            ThumbnailTier.Medium => (120, 68, Math.Max(2000, durationMs / 20)),
            ThumbnailTier.High => (160, 90, Math.Max(1000, durationMs / 40)),
            _ => (120, 68, Math.Max(2000, durationMs / 20))
        };
    }

    /// <summary>
    /// 스트립 메모리 예상치
    /// </summary>
    private static long EstimateStripMemory(uint w, uint h, long durationMs, long intervalMs)
    {
        int thumbCount = (int)(durationMs / intervalMs) + 1;
        return thumbCount * w * h * 4L; // RGBA
    }

    /// <summary>
    /// 메모리 예산 초과 시 LRU eviction
    /// </summary>
    private void EvictIfNeeded(long additionalBytes)
    {
        while ((_currentMemoryBytes + additionalBytes > MaxMemoryBytes || _cache.Count >= MaxStrips)
               && _lruOrder.Count > 0)
        {
            var oldestKey = _lruOrder.First!.Value;
            RemoveStrip(oldestKey);
        }
    }

    private void RemoveStrip(ThumbnailStripKey key)
    {
        if (_cache.TryGetValue(key, out var strip))
        {
            _currentMemoryBytes -= strip.MemoryBytes;
            strip.Thumbnails.Clear();
            _cache.Remove(key);
        }
        _lruOrder.Remove(key);
    }

    /// <summary>
    /// 백그라운드 썸네일 생성 (점진적)
    /// 각 썸네일 생성 완료 시 UI에 알림 → 즉시 표시
    /// </summary>
    private async Task GenerateStripAsync(
        ThumbnailStripKey key, ThumbnailStrip strip,
        uint thumbWidth, uint thumbHeight,
        CancellationToken ct)
    {
        await _generationSemaphore.WaitAsync(ct);
        try
        {
            long timeMs = 0;
            while (timeMs <= strip.DurationMs && !ct.IsCancellationRequested)
            {
                try
                {
                    // Rust FFI 호출 (배경 스레드에서 안전)
                    byte[]? rgbaData = null;
                    uint actualWidth = 0, actualHeight = 0;

                    await Task.Run(() =>
                    {
                        using var frame = RenderService.GenerateThumbnail(
                            strip.FilePath, timeMs, thumbWidth, thumbHeight);

                        if (frame != null)
                        {
                            rgbaData = frame.Data.ToArray();
                            actualWidth = frame.Width;
                            actualHeight = frame.Height;
                        }
                    }, ct);

                    if (rgbaData != null && actualWidth > 0 && actualHeight > 0)
                    {
                        // UI 스레드에서 WriteableBitmap 생성
                        var capturedData = rgbaData;
                        var capturedW = actualWidth;
                        var capturedH = actualHeight;
                        var capturedTimeMs = timeMs;

                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (ct.IsCancellationRequested) return;

                            var pixelSize = new Avalonia.PixelSize((int)capturedW, (int)capturedH);
                            var dpi = new Avalonia.Vector(96, 96);
                            var bitmap = new WriteableBitmap(pixelSize, dpi,
                                Avalonia.Platform.PixelFormat.Rgba8888,
                                Avalonia.Platform.AlphaFormat.Unpremul);

                            using (var buffer = bitmap.Lock())
                            {
                                unsafe
                                {
                                    fixed (byte* srcPtr = capturedData)
                                    {
                                        var dst = (byte*)buffer.Address;
                                        var size = (int)capturedW * (int)capturedH * 4;
                                        Buffer.MemoryCopy(srcPtr, dst, size, size);
                                    }
                                }
                            }

                            strip.Thumbnails.Add(new CachedThumbnail
                            {
                                SourceTimeMs = capturedTimeMs,
                                Bitmap = bitmap
                            });

                            long bitmapBytes = capturedW * capturedH * 4L;
                            strip.MemoryBytes += bitmapBytes;
                            _currentMemoryBytes += bitmapBytes;
                        });

                        // 점진적 표시: 썸네일 추가될 때마다 다시 그리기
                        OnThumbnailReady?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 개별 썸네일 실패 → 스킵 (전체 중단 방지)
                    System.Diagnostics.Debug.WriteLine(
                        $"Thumbnail generation failed at {timeMs}ms: {ex.Message}");
                }

                timeMs += strip.IntervalMs;
            }

            strip.IsComplete = true;
            strip.IsGenerating = false;
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }
}
```

### 2.4 수정 파일: `VortexCut.UI/Controls/TimelineCanvas.cs`

#### 변경 내용: ThumbnailStripService 생성 + ClipCanvasPanel 주입

```csharp
// TimelineCanvas.cs 필드 추가
private readonly ThumbnailStripService _thumbnailService;

// 생성자에서 초기화
public TimelineCanvas()
{
    // ... 기존 코드 ...
    _thumbnailService = new ThumbnailStripService();
    _thumbnailService.OnThumbnailReady = () =>
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            _clipCanvasPanel.InvalidateVisual,
            Avalonia.Threading.DispatcherPriority.Render);
    };
    _clipCanvasPanel.SetThumbnailService(_thumbnailService);
}

// IDisposable 구현 or DetachedFromVisualTree에서 정리
protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
{
    base.OnDetachedFromVisualTree(e);
    _thumbnailService?.Dispose();
}
```

### 2.5 수정 파일: `VortexCut.UI/Controls/Timeline/ClipCanvasPanel.cs`

#### 변경 1: 필드 + 주입 메서드 추가

```csharp
// 필드 추가 (기존 필드 블록에)
private ThumbnailStripService? _thumbnailStripService;

// 주입 메서드
public void SetThumbnailService(ThumbnailStripService service)
{
    _thumbnailStripService = service;
}
```

#### 변경 2: DrawClip()에 썸네일 렌더링 삽입

삽입 위치: `DrawClip()` 메서드 내부, 클립 본체 gradient 그린 직후 + 클립 이름/테두리 그리기 전.

현재 DrawClip() 구조 (LOD Full 기준):
1. 그림자 그리기
2. 드래그 하이라이트
3. 선택 글로우
4. 호버 하이라이트
5. **클립 본체 gradient 그리기** ← 여기 뒤에 삽입
6. **>>> 썸네일 스트립 렌더링 (새로 추가) <<<**
7. 테두리 그리기
8. 트림 핸들
9. 아이콘
10. 클립 이름/시간 텍스트
11. 뮤트 오버레이

```csharp
// 비디오 클립 + LOD Full/Medium일 때 썸네일 렌더링
if (!isAudioClip && lod != ClipLOD.Minimal && _thumbnailStripService != null)
{
    var tier = ThumbnailStripService.GetTierForZoom(_pixelsPerMs);
    var strip = _thumbnailStripService.GetOrRequestStrip(
        clip.FilePath, clip.DurationMs, tier);

    if (strip?.Thumbnails.Count > 0)
    {
        DrawThumbnailStrip(context, strip, clipRect, clip);
    }
}
```

#### 변경 3: DrawThumbnailStrip() 신규 메서드

```csharp
/// <summary>
/// 클립 내부에 썸네일 스트립 렌더링
/// 클립 본체 gradient 위에 반투명 썸네일을 overlapping 배치
/// </summary>
private void DrawThumbnailStrip(
    DrawingContext context, ThumbnailStrip strip,
    Rect clipRect, ClipModel clip)
{
    // 썸네일 표시 영역 (클립 상하 5px 마진)
    double thumbMargin = 5;
    double thumbHeight = clipRect.Height - thumbMargin * 2;
    if (thumbHeight <= 0) return;

    double aspectRatio = 16.0 / 9.0;
    double thumbWidth = thumbHeight * aspectRatio;

    // 클립 영역으로 클리핑 (썸네일이 클립 밖으로 안 나가도록)
    using (context.PushClip(clipRect))
    {
        foreach (var thumb in strip.Thumbnails)
        {
            // 클립 내 시간 위치 → 픽셀 위치
            double thumbX = clipRect.X + (thumb.SourceTimeMs * _pixelsPerMs);

            // 뷰포트 밖 스킵 (성능)
            if (thumbX + thumbWidth < 0 || thumbX > Bounds.Width)
                continue;

            var destRect = new Rect(
                thumbX,
                clipRect.Y + thumbMargin,
                thumbWidth,
                thumbHeight);

            context.DrawImage(thumb.Bitmap, destRect);
        }

        // 썸네일 위에 반투명 오버레이 (클립 색상 유지)
        // 클립의 gradient 색상을 매우 반투명하게 위에 얹음
        var overlayBrush = RenderResourceCache.GetSolidBrush(
            Color.FromArgb(80, clip.LabelColor.R, clip.LabelColor.G, clip.LabelColor.B));
        context.FillRectangle(overlayBrush, clipRect);
    }
}
```

> **주의**: `clip.LabelColor` 접근 시 `ClipModel`에 `LabelColor` 속성이 있는지 확인 필요.
> 없으면 트랙 타입에 따른 기본 색상 사용 (비디오: #3A7BF2 계열).

### 2.6 줌 레벨 변경 시 티어 전환

줌 변경 시 `_pixelsPerMs`가 변하므로 `GetTierForZoom()` 반환값이 바뀜.
- 이전 티어의 캐시는 LRU에 유지 (다시 줌하면 재사용)
- 새 티어 캐시 미스 → 백그라운드 생성 시작
- 티어 전환 중에는 이전 티어의 썸네일이 표시되다가 새 티어로 교체

### 2.7 썸네일 간격 & 메모리 예산

| 티어 | 해상도 | 간격 공식 | 10초 클립 | 60초 클립 | 메모리/클립 |
|------|--------|-----------|-----------|-----------|-------------|
| Low | 80×45 | max(5s, dur/10) | 2개 | 12개 | 14KB~168KB |
| Medium | 120×68 | max(2s, dur/20) | 5개 | 30개 | 163KB~978KB |
| High | 160×90 | max(1s, dur/40) | 10개 | 60개 | 576KB~3.5MB |

**프로젝트 기준 (20개 비디오 클립, Medium 티어)**:
- 평균 30초 클립 × 15개 썸네일 × 33KB = ~10MB (매우 안전)

### 2.8 테스트 계획

```
기능 테스트:
1. 비디오 클립 추가 → 2-3초 내 썸네일 점진적 표시
2. Ctrl+Wheel 줌 → Low/Medium/High 티어 자동 전환
3. 오디오 클립 → 썸네일 없음 (기존 파형만)
4. 한글 파일 경로 → 정상 동작 (UTF-8 마샬링)
5. 20개 클립 로드 → 메모리 256MB 미만
6. 프로젝트 전환 → ClearAll() 호출, 메모리 해제

회귀 테스트:
- dotnet test VortexCut.Tests (기존 110개 통과)
- 재생, 스크럽, 드래그, 트림, 키프레임 → 모두 정상
```

---

## Phase 3: Dirty 플래그 + 애니메이션 빈도 조절

### 3.1 문제 분석

#### 문제 A: 선택 글로우 애니메이션 60fps 루프
```csharp
// ClipCanvasPanel.cs Render() 내부 (line ~162-165)
if (_viewModel?.SelectedClips.Count > 0)
{
    Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
}
```
클립 하나만 선택해도 **매 프레임** InvalidateVisual() → Render() → 모든 트랙+클립 재그리기.
선택 글로우 펄스 애니메이션은 시각적으로 10fps로도 충분 (사인 곡선이 느린 주기).

#### 문제 B: 재생 중 Playhead 글로우도 60fps
재생 중에는 Playhead 위치 업데이트 때문에 이미 Render()가 호출됨.
Playhead 글로우 애니메이션은 재생 렌더링에 무임승차하므로 별도 문제 없음.

#### 문제 C: 유휴 상태에서 불필요한 Render()
아무 조작 없이 클립만 선택한 상태 → 글로우 애니메이션 때문에 무한 렌더링.
CPU 50% 점유 (Render loop → TrackBG + Clips + Playhead + HUD 전부 다시 그림).

### 3.2 해결 A: 선택 글로우 애니메이션 10fps 제한

```csharp
// ClipCanvasPanel.cs 필드 추가
private double _glowAccumulatorMs = 0;
private const double GlowIntervalMs = 100; // 10fps

// Render() 내부 변경
_selectionPulsePhase += deltaTime * 0.002;
if (_selectionPulsePhase > Math.PI * 2)
    _selectionPulsePhase -= Math.PI * 2;

// 변경: 100ms마다만 InvalidateVisual (10fps)
if (_viewModel?.SelectedClips.Count > 0 && !_viewModel.IsPlaying)
{
    _glowAccumulatorMs += deltaTime;
    if (_glowAccumulatorMs >= GlowIntervalMs)
    {
        _glowAccumulatorMs = 0;
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }
}
// 재생 중에는 Playhead 업데이트가 Render를 트리거하므로 여기서 안 해도 됨
```

**효과**: 유휴 상태 CPU 50% → ~8% (10fps × full render)

### 3.3 해결 B: 스냅샷 기반 변경 감지 (트랙 배경 조건부 스킵)

트랙 배경은 줌/스크롤/트랙 변경 없으면 동일. 변경 감지로 불필요한 재그리기 스킵.

```csharp
// ClipCanvasPanel.cs 필드 추가
private double _lastRenderedPixelsPerMs = -1;
private double _lastRenderedScrollOffsetX = -1;
private int _lastRenderedVideoTrackCount = -1;
private int _lastRenderedAudioTrackCount = -1;
private bool _trackBackgroundDirty = true;

// Render() 내부에서 변경 감지
bool zoomChanged = Math.Abs(_pixelsPerMs - _lastRenderedPixelsPerMs) > 0.0001;
bool scrollChanged = Math.Abs(_scrollOffsetX - _lastRenderedScrollOffsetX) > 0.5;
bool trackLayoutChanged = _videoTracks.Count != _lastRenderedVideoTrackCount
                        || _audioTracks.Count != _lastRenderedAudioTrackCount;

_trackBackgroundDirty = zoomChanged || scrollChanged || trackLayoutChanged;

// DrawTrackBackgrounds 조건부 호출
// NOTE: Avalonia 11에는 RenderTargetBitmap이 없으므로 캐싱 불가.
//       대신 변경 없으면 이전 DrawTrackBackgrounds의 결과가
//       context에 그대로 남으므로... 실제로는 DrawingContext가
//       매 프레임 새로 생성되어 스킵 불가능.
//
// → 실질적 최적화: _trackBackgroundDirty가 false이면
//   gradient 계산/브러시 할당 생략 + 단순 FillRectangle만 수행
//   (RenderResourceCache 적용으로 이미 상당 부분 달성)

_lastRenderedPixelsPerMs = _pixelsPerMs;
_lastRenderedScrollOffsetX = _scrollOffsetX;
_lastRenderedVideoTrackCount = _videoTracks.Count;
_lastRenderedAudioTrackCount = _audioTracks.Count;
```

> **결론**: Avalonia 11의 immediate-mode DrawingContext 특성상 "트랙 배경 그리기 완전 스킵"은 불가능.
> Phase 1의 브러시 캐싱이 이 영역의 실질적 최적화를 이미 달성함.
> Phase 3에서는 **애니메이션 빈도 조절**이 핵심 최적화.

### 3.4 해결 C: 클립 개수별 LOD 자동 조절

화면에 많은 클립이 보일 때 LOD를 자동으로 낮춰 렌더링 부하 감소.

```csharp
// Render() 시작부에서 visible 클립 수 체크
int visibleClipCount = 0;
long visibleStartMs = XToTime(-50);
long visibleEndMs = XToTime(Bounds.Width + 50);
foreach (var clip in _clips)
{
    long clipEnd = clip.StartTimeMs + clip.DurationMs;
    if (clipEnd >= visibleStartMs && clip.StartTimeMs <= visibleEndMs)
        visibleClipCount++;
}

// 50개 이상이면 LOD 강제 하향
bool forceLowLOD = visibleClipCount > 50;

// DrawClip() 호출부에서:
// var lod = GetLOD(clipWidthPx);
// if (forceLowLOD && lod == ClipLOD.Full) lod = ClipLOD.Medium;
```

### 3.5 변경 요약

| 변경 | 파일 | 효과 |
|------|------|------|
| 글로우 10fps 제한 | ClipCanvasPanel.cs Render() | 유휴 CPU 50% → 8% |
| 스냅샷 변경 감지 | ClipCanvasPanel.cs Render() | 향후 캐싱 확장 기반 |
| 클립 수 LOD 자동 조절 | ClipCanvasPanel.cs Render()/DrawClips() | 대규모 프로젝트 성능 |

---

## 구현 순서 & 의존성

```
Phase 2 (썸네일 스트립):
  1. ThumbnailStripService.cs 생성 (독립적)
  2. TimelineCanvas.cs에 서비스 생성 + 주입
  3. ClipCanvasPanel.cs에 SetThumbnailService() + DrawThumbnailStrip() 추가
  4. DrawClip()에 썸네일 렌더링 삽입
  5. 빌드 + 기능 테스트

Phase 3 (Dirty 플래그 + 애니메이션):
  6. ClipCanvasPanel.cs에 _glowAccumulatorMs 추가
  7. Render()의 InvalidateVisual 로직 수정
  8. 스냅샷 변경 감지 필드 추가
  9. 클립 수 LOD 자동 조절 추가
  10. 빌드 + 성능 검증

Phase 2와 3은 독립적이므로 병렬 구현 가능.
```

---

## 파일 목록

### 새 파일
| 파일 | 줄 수 (예상) | 역할 |
|------|-------------|------|
| `VortexCut.UI/Services/ThumbnailStripService.cs` | ~280줄 | 썸네일 스트립 생성/캐시/LRU |

### 수정 파일
| 파일 | 변경 범위 | 역할 |
|------|----------|------|
| `VortexCut.UI/Controls/TimelineCanvas.cs` | +15줄 | 서비스 생성 + 주입 |
| `VortexCut.UI/Controls/Timeline/ClipCanvasPanel.cs` | +60줄, ~10줄 수정 | 썸네일 렌더링 + 애니메이션 조절 |

### Rust 변경 없음
기존 `generate_video_thumbnail` FFI가 완전 동작하므로 Rust 측 수정 불필요.

---

## 검증 명령

```bash
# 빌드
dotnet build VortexCut.UI -c Debug

# 회귀 테스트
dotnet test VortexCut.Tests

# Rust 테스트
cd rust-engine && cargo test
```

---

**마지막 업데이트**: 2026-02-12 (Phase 2 & 3 구현 완료)

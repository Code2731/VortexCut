using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VortexCut.Interop.Services;

namespace VortexCut.UI.Services;

/// <summary>
/// 줌 레벨별 썸네일 해상도 티어
/// </summary>
public enum ThumbnailTier
{
    Low,    // 80x45  (극 축소, _pixelsPerMs < 0.05)
    Medium, // 120x68 (보통 줌, 0.05 ~ 0.3)
    High    // 160x90 (확대, > 0.3)
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

    // 파일별 최근 요청된 Visible Range (클립 로컬 시간 기준)
    private readonly Dictionary<string, (long StartMs, long EndMs)> _visibleRanges = new();

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
    /// 특정 미디어 파일에 대해, 클립 로컬 기준의 가시 구간(Visible Range)을 업데이트.
    /// GenerateStripAsync에서 우선적으로 디코딩할 시간대의 힌트로 사용된다.
    /// </summary>
    public void UpdateVisibleRange(string filePath, long startMs, long endMs)
    {
        if (endMs < startMs)
        {
            (startMs, endMs) = (endMs, startMs);
        }

        if (startMs < 0) startMs = 0;

        _visibleRanges[filePath] = (startMs, endMs);
    }

    /// <summary>
    /// 줌 레벨에서 적절한 ThumbnailTier 계산
    /// 기본 pixelsPerMs=0.1, 줌 스텝 *1.1/*0.9 기준:
    ///   Low:    < 0.08 (줌아웃 3회: 0.1*0.9^3 = 0.073)
    ///   Medium: 0.08 ~ 0.15 (기본값 0.1 근처)
    ///   High:   > 0.15 (줌인 5회: 0.1*1.1^5 = 0.161)
    /// </summary>
    public static ThumbnailTier GetTierForZoom(double pixelsPerMs)
    {
        if (pixelsPerMs < 0.08) return ThumbnailTier.Low;
        if (pixelsPerMs < 0.15) return ThumbnailTier.Medium;
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
            strip.Thumbnails.Clear();
        }
        _cache.Clear();
        _lruOrder.Clear();
        _currentMemoryBytes = 0;
        _visibleRanges.Clear();
    }

    /// <summary>
    /// 특정 파일의 시간 구간에 해당하는 썸네일만 무효화.
    /// 트림/이동/삭제 등 편집 작업 이후 부분적인 재생성을 가능하게 한다.
    /// </summary>
    public void InvalidateRange(string filePath, long startMs, long endMs)
    {
        if (endMs < startMs)
        {
            (startMs, endMs) = (endMs, startMs);
        }

        foreach (var (key, strip) in _cache.ToList())
        {
            if (!string.Equals(key.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                continue;

            // 해당 구간에 포함되는 썸네일만 제거
            var removed = strip.Thumbnails
                .Where(t => t.SourceTimeMs >= startMs && t.SourceTimeMs <= endMs)
                .ToList();

            if (removed.Count == 0)
                continue;

            foreach (var thumb in removed)
            {
                strip.Thumbnails.Remove(thumb);
                long bytes = thumb.Bitmap.PixelSize.Width * thumb.Bitmap.PixelSize.Height * 4L;
                strip.MemoryBytes -= bytes;
                _currentMemoryBytes -= bytes;
            }

            // 일부가 삭제되었으므로, 스트립을 부분적으로 다시 채워야 한다.
            // 다음 GetOrRequestStrip 호출 시 GenerateStripAsync에서 보완되도록
            // IsComplete 플래그만 false로 돌려둔다.
            strip.IsComplete = false;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이미 해제된 경우 무시
        }

        foreach (var strip in _cache.Values)
        {
            strip.Thumbnails.Clear();
        }
        _cache.Clear();
        _lruOrder.Clear();
        _visibleRanges.Clear();
        _currentMemoryBytes = 0;

        _generationSemaphore.Dispose();
        _cts.Dispose();
    }

    // === 내부 구현 ===

    /// <summary>
    /// 티어별 파라미터 계산
    /// 모든 티어에 상한 간격 적용 → 긴 비디오에서도 연속 Filmstrip 보장
    ///
    /// 메모리 예산 (RGBA):
    ///   Low:    80×45×4  ≈ 14KB/장
    ///   Medium: 120×68×4 ≈ 33KB/장
    ///   High:   160×90×4 ≈ 58KB/장
    ///
    /// intervalMs는 Temporal Compression: 1초당 1프레임 위주로 CPU/GPU 절감
    ///   - Low    : 1초 간격 (GOP 키프레임 위주)
    ///   - Medium : 1초 간격
    ///   - High   : 0.8초 간격 (확대 시 약간 밀도 높임)
    /// </summary>
    private static (uint thumbWidth, uint thumbHeight, long intervalMs) GetTierParams(
        ThumbnailTier tier, long durationMs)
    {
        return tier switch
        {
            ThumbnailTier.Low => (80, 45, 1000),
            ThumbnailTier.Medium => (120, 68, 1000),
            ThumbnailTier.High => (160, 90, 800),
            _ => (120, 68, 1000)
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
    /// 백그라운드 썸네일 생성 (세션 기반, 점진적)
    /// 디코더를 한 번만 열고 시간순으로 전체 썸네일 생성
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
            // 이미 존재하는 썸네일 시간을 기록하여 중복 생성 방지
            var existingTimes = new HashSet<long>(strip.Thumbnails.Select(t => t.SourceTimeMs));
            var timeOrder = BuildTimeOrder(strip, existingTimes);

            if (timeOrder.Count == 0)
            {
                strip.IsComplete = true;
                strip.IsGenerating = false;
                return;
            }

            // 세션 기반: 디코더 1회 Open → 전체 썸네일 생성 → Close
            // 기존: 썸네일마다 Decoder Open/Close (N회)
            await Task.Run(() =>
            {
                ThumbnailSession? session = null;
                try
                {
                    session = ThumbnailSession.Create(strip.FilePath, thumbWidth, thumbHeight);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"ThumbnailStrip: Session create failed: {ex.Message}");
                    return;
                }

                try
                {
                    foreach (var timeMs in timeOrder)
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        // 중복 체크
                        if (strip.Thumbnails.Any(t => t.SourceTimeMs == timeMs))
                            continue;

                        try
                        {
                            using var frame = session.Generate(timeMs);

                            if (frame == null || frame.Width == 0 || frame.Height == 0)
                                continue;

                            var rgbaData = frame.Data.ToArray();
                            var actualWidth = frame.Width;
                            var actualHeight = frame.Height;
                            var capturedTimeMs = timeMs;

                            // UI 스레드에서 WriteableBitmap 생성
                            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (ct.IsCancellationRequested) return;

                                var pixelSize = new PixelSize((int)actualWidth, (int)actualHeight);
                                var dpi = new Vector(96, 96);
                                var bitmap = new WriteableBitmap(pixelSize, dpi,
                                    Avalonia.Platform.PixelFormat.Rgba8888,
                                    AlphaFormat.Unpremul);

                                using (var buffer = bitmap.Lock())
                                {
                                    unsafe
                                    {
                                        fixed (byte* srcPtr = rgbaData)
                                        {
                                            var dst = (byte*)buffer.Address;
                                            var size = (int)actualWidth * (int)actualHeight * 4;
                                            Buffer.MemoryCopy(srcPtr, dst, size, size);
                                        }
                                    }
                                }

                                strip.Thumbnails.Add(new CachedThumbnail
                                {
                                    SourceTimeMs = capturedTimeMs,
                                    Bitmap = bitmap
                                });

                                // 이진 탐색용 정렬 유지
                                strip.Thumbnails.Sort(
                                    (a, b) => a.SourceTimeMs.CompareTo(b.SourceTimeMs));

                                long bitmapBytes = actualWidth * actualHeight * 4L;
                                strip.MemoryBytes += bitmapBytes;
                                _currentMemoryBytes += bitmapBytes;
                            }).GetAwaiter().GetResult();

                            // 점진적 표시
                            OnThumbnailReady?.Invoke();
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"ThumbnailStrip: Thumb failed at {timeMs}ms: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    session.Dispose();
                }
            }, ct);

            strip.IsComplete = true;
            strip.IsGenerating = false;
        }
        catch (OperationCanceledException)
        {
            // 취소됨
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    /// <summary>
    /// 썸네일 생성 시간 목록 계산.
    /// - 파일별 Visible Range 힌트가 있으면: 그 구간의 중앙부터 양옆으로 확장
    ///   (현재 화면에 보이는 구간을 우선적으로 생성)
    /// - 그 외 나머지 구간은 0ms부터 Duration까지 interval 간격으로 채움
    /// 이미 존재하는 썸네일 시간(existingTimes)은 제외한다.
    /// </summary>
    private List<long> BuildTimeOrder(ThumbnailStrip strip, HashSet<long> existingTimes)
    {
        var order = new List<long>();

        long duration = strip.DurationMs;
        long interval = Math.Max(strip.IntervalMs, 1);

        // 파일 단위 Visible Range 힌트 조회 (클립 로컬 시간 기준)
        (long StartMs, long EndMs) range;
        bool hasRange = _visibleRanges.TryGetValue(strip.FilePath, out range);

        if (hasRange)
        {
            long clampedStart = Math.Clamp(range.StartMs, 0, duration);
            long clampedEnd = Math.Clamp(range.EndMs, 0, duration);
            if (clampedEnd < clampedStart)
            {
                (clampedStart, clampedEnd) = (clampedEnd, clampedStart);
            }

            // Viewport 양쪽 50% margin 프리페치 (스크롤 시 즉시 표시)
            long viewportWidth = clampedEnd - clampedStart;
            long margin = viewportWidth / 2;
            clampedStart = Math.Max(0, clampedStart - margin);
            clampedEnd = Math.Min(duration, clampedEnd + margin);

            long center = (clampedStart + clampedEnd) / 2;

            // 중앙부터 ±n * interval 순서로 확장
            if (!existingTimes.Contains(center))
            {
                order.Add(center);
            }

            for (int n = 1; ; n++)
            {
                long t1 = center + n * interval;
                long t2 = center - n * interval;
                bool added = false;

                if (t1 <= duration)
                {
                    if (!existingTimes.Contains(t1))
                    {
                        order.Add(t1);
                        added = true;
                    }
                }

                if (t2 >= 0)
                {
                    if (!existingTimes.Contains(t2))
                    {
                        order.Add(t2);
                        added = true;
                    }
                }

                if (!added && (t1 > duration && t2 < 0))
                    break;
            }
        }

        // 나머지 시간대는 0ms부터 Duration까지 순차로 채움
        for (long t = 0; t <= duration; t += interval)
        {
            if (!existingTimes.Contains(t) && !order.Contains(t))
            {
                order.Add(t);
            }
        }

        return order;
    }
}

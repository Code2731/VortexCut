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

                            var pixelSize = new PixelSize((int)capturedW, (int)capturedH);
                            var dpi = new Vector(96, 96);
                            var bitmap = new WriteableBitmap(pixelSize, dpi,
                                Avalonia.Platform.PixelFormat.Rgba8888,
                                AlphaFormat.Unpremul);

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

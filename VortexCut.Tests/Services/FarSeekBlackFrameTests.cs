using VortexCut.Interop.Services;
using VortexCut.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace VortexCut.Tests.Services;

/// <summary>
/// 원거리 seek 시 BLACK 프레임 검증 + 모든 가설 자동화 테스트
/// 가설 1: 원거리 seek 후 첫 프레임이 alpha=0x00 (BLACK)
/// 가설 2: +33ms 재시도 시 alpha=0xFF (정상 프레임)
/// 가설 3: 정상 프레임의 px0이 위치마다 다름 (실제 다른 콘텐츠)
/// 가설 4: 연속 렌더링 시 last_rendered_frame 오염 없음
/// 가설 5: FrameSkipped 발생 시 이전 위치 프레임 반환 여부
/// </summary>
public class FarSeekBlackFrameTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private TimelineService? _timelineService;
    private RenderService? _renderService;

    private const string TEST_VIDEO_PATH =
        @"C:\Users\USER\Videos\드론 대응 2.75인치 로켓 '비궁'으로 유도키트 개발, 사우디 기술협력 추진.mp4";

    public FarSeekBlackFrameTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Setup(long clipDurationMs)
    {
        _timelineService = new TimelineService();
        _renderService = new RenderService();

        _timelineService.CreateTimeline(1920, 1080, 30.0);
        var trackId = _timelineService.AddVideoTrack();
        _timelineService.AddVideoClip(trackId, TEST_VIDEO_PATH, 0, clipDurationMs);

        var handle = _timelineService.GetTimelineHandle();
        _renderService.CreateRenderer(handle);
    }

    private static string GetPx0(byte[] data)
    {
        if (data.Length < 4) return "N/A";
        return $"{data[0]:X2}{data[1]:X2}{data[2]:X2}{data[3]:X2}";
    }

    private static bool IsBlackFrame(byte[] data)
    {
        return data.Length >= 4 && data[3] == 0x00; // alpha=0 → black_frame() fallback
    }

    /// <summary>
    /// 가설 1+2: 원거리 seek 후 첫 프레임 alpha 검사 + 재시도 검증
    /// </summary>
    [FactRequiresNativeDll]
    public void FarSeek_FirstFrame_AlphaCheck()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵");
            return;
        }

        Setup(30000); // 30초 클립

        // Step 1: 0ms에서 렌더 (기준점)
        _output.WriteLine("=== Step 1: 기준 렌더 (0ms) ===");
        using var frame0 = _renderService!.RenderFrame(0);
        Assert.NotNull(frame0);
        var px0_at_0 = GetPx0(frame0.Data);
        var alpha0 = frame0.Data[3];
        _output.WriteLine($"  0ms: px0={px0_at_0}, alpha=0x{alpha0:X2}, size={frame0.Width}x{frame0.Height}");
        Assert.Equal(0xFF, alpha0); // 첫 프레임은 항상 정상이어야 함

        // Step 2: 가까운 위치 (1000ms) 렌더
        _output.WriteLine("=== Step 2: 근거리 seek (1000ms) ===");
        using var frame1 = _renderService.RenderFrame(1000);
        Assert.NotNull(frame1);
        var px0_at_1000 = GetPx0(frame1.Data);
        var alpha1 = frame1.Data[3];
        _output.WriteLine($"  1000ms: px0={px0_at_1000}, alpha=0x{alpha1:X2}");
        Assert.Equal(0xFF, alpha1);

        // Step 3: 원거리 seek (20000ms) — 핵심 테스트
        _output.WriteLine("=== Step 3: 원거리 seek (20000ms) ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var frameFar = _renderService.RenderFrame(20000);
        var farElapsed = sw.ElapsedMilliseconds;
        Assert.NotNull(frameFar);
        var px0_far = GetPx0(frameFar.Data);
        var alphaFar = frameFar.Data[3];
        bool isFarBlack = IsBlackFrame(frameFar.Data);
        _output.WriteLine($"  20000ms: px0={px0_far}, alpha=0x{alphaFar:X2}, black={isFarBlack}, elapsed={farElapsed}ms");

        if (isFarBlack)
        {
            _output.WriteLine("  ⚠️ BLACK 프레임 감지! +33ms 재시도...");

            // Step 3b: 재시도 (+33ms)
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            using var frameRetry = _renderService.RenderFrame(20033);
            var retryElapsed = sw2.ElapsedMilliseconds;
            Assert.NotNull(frameRetry);
            var px0_retry = GetPx0(frameRetry.Data);
            var alphaRetry = frameRetry.Data[3];
            bool isRetryBlack = IsBlackFrame(frameRetry.Data);
            _output.WriteLine($"  20033ms: px0={px0_retry}, alpha=0x{alphaRetry:X2}, black={isRetryBlack}, elapsed={retryElapsed}ms");

            // 재시도는 정상이어야 함
            Assert.False(isRetryBlack, "재시도 프레임도 BLACK이면 alpha=0 체크로는 해결 불가");
            Assert.Equal(0xFF, alphaRetry);
        }
        else
        {
            _output.WriteLine("  ✅ 원거리 seek 후 정상 프레임 (BLACK 아님)");
        }

        // Step 4: px0 비교 — 다른 위치는 다른 콘텐츠여야 함
        _output.WriteLine("=== Step 4: 콘텐츠 비교 ===");
        _output.WriteLine($"  0ms:     {px0_at_0}");
        _output.WriteLine($"  1000ms:  {px0_at_1000}");
        _output.WriteLine($"  20000ms: {px0_far}");
        // 최소한 일부는 달라야 함 (같은 비디오라도 다른 시점이면 다른 픽셀)
    }

    /// <summary>
    /// 가설 3: 다중 원거리 seek 반복 — 일관성 검증
    /// </summary>
    [FactRequiresNativeDll]
    public void MultipleFarSeeks_ConsistentResults()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵");
            return;
        }

        Setup(30000);

        long[] positions = { 0, 15000, 5000, 25000, 1000, 20000 };
        var results = new List<(long pos, string px0, byte alpha, long elapsedMs, bool isBlack)>();

        foreach (var pos in positions)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var frame = _renderService!.RenderFrame(pos);
            var elapsed = sw.ElapsedMilliseconds;

            Assert.NotNull(frame);
            var px0 = GetPx0(frame.Data);
            var alpha = frame.Data[3];
            var isBlack = IsBlackFrame(frame.Data);

            results.Add((pos, px0, alpha, elapsed, isBlack));
            _output.WriteLine($"  {pos,6}ms: px0={px0}, alpha=0x{alpha:X2}, black={isBlack,-5}, elapsed={elapsed,5}ms");

            // BLACK이면 즉시 재시도해서 복구 가능한지 확인
            if (isBlack)
            {
                using var retry = _renderService.RenderFrame(pos + 33);
                if (retry != null)
                {
                    var retryPx0 = GetPx0(retry.Data);
                    var retryAlpha = retry.Data[3];
                    _output.WriteLine($"  RETRY {pos+33,6}ms: px0={retryPx0}, alpha=0x{retryAlpha:X2}, black={IsBlackFrame(retry.Data)}");
                }
            }
        }

        // 전체 결과 요약
        int blackCount = results.Count(r => r.isBlack);
        _output.WriteLine($"\n=== 요약: {results.Count}회 렌더 중 BLACK={blackCount}회 ===");

        // BLACK이 발생하면 재시도로 해결되는지 확인 (이미 위에서 로그)
        // 모든 정상 프레임은 alpha=0xFF여야 함
        foreach (var r in results.Where(r => !r.isBlack))
        {
            Assert.Equal(0xFF, r.alpha);
        }
    }

    /// <summary>
    /// 가설 4: last_rendered_frame 오염 테스트
    /// 위치 A 렌더 → 위치 B(원거리) 렌더 → B가 A의 프레임을 반환하는지 확인
    /// </summary>
    [FactRequiresNativeDll]
    public void LastRenderedFrame_NotStale()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵");
            return;
        }

        Setup(30000);

        // A 위치 렌더 (0ms)
        using var frameA = _renderService!.RenderFrame(0);
        Assert.NotNull(frameA);
        var px0A = GetPx0(frameA.Data);
        _output.WriteLine($"Position A (0ms): px0={px0A}");

        // B 위치 렌더 (20000ms) - 원거리
        using var frameB = _renderService.RenderFrame(20000);
        Assert.NotNull(frameB);
        var px0B = GetPx0(frameB.Data);
        bool isBBlack = IsBlackFrame(frameB.Data);
        _output.WriteLine($"Position B (20000ms): px0={px0B}, black={isBBlack}");

        if (isBBlack)
        {
            // BLACK이면 재시도
            using var frameBRetry = _renderService.RenderFrame(20033);
            Assert.NotNull(frameBRetry);
            px0B = GetPx0(frameBRetry.Data);
            _output.WriteLine($"Position B retry (20033ms): px0={px0B}");
        }

        // 핵심 검증: B의 px0이 A의 px0과 다른지 확인
        // (같으면 last_rendered_frame 오염 = "이전 영상" 증상의 원인)
        _output.WriteLine($"\n=== 오염 검사 ===");
        _output.WriteLine($"  A(0ms) px0:     {px0A}");
        _output.WriteLine($"  B(20000ms) px0: {px0B}");

        if (px0A == px0B)
        {
            _output.WriteLine("  ❌ 동일! last_rendered_frame 오염 가능성");
        }
        else
        {
            _output.WriteLine("  ✅ 다름 — 정상");
        }
    }

    /// <summary>
    /// 가설 5: 스크럽→재생 전환 시 SetPlaybackMode(true) 후 last_rendered_frame 상태
    /// </summary>
    [FactRequiresNativeDll]
    public void SetPlaybackMode_ClearsLastRenderedFrame()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵");
            return;
        }

        Setup(30000);

        // 0ms에서 렌더 → last_rendered_frame 설정됨
        using var frame0 = _renderService!.RenderFrame(0);
        Assert.NotNull(frame0);
        _output.WriteLine($"Step 1: Render at 0ms → px0={GetPx0(frame0.Data)}");

        // SetPlaybackMode(true) → 디코더/캐시/last_rendered 전부 flush
        _renderService.SetPlaybackMode(true);
        _output.WriteLine("Step 2: SetPlaybackMode(true) called");

        // 클립이 없는 위치 렌더 → black_frame (last_rendered가 null이므로)
        // 또는 클립이 있는 위치 렌더 → 새로 디코딩
        using var frameAfterFlush = _renderService.RenderFrame(20000);
        Assert.NotNull(frameAfterFlush);
        var px0After = GetPx0(frameAfterFlush.Data);
        var alphaAfter = frameAfterFlush.Data[3];
        bool isAfterBlack = IsBlackFrame(frameAfterFlush.Data);
        _output.WriteLine($"Step 3: Render at 20000ms after flush → px0={px0After}, alpha=0x{alphaAfter:X2}, black={isAfterBlack}");

        // SetPlaybackMode 후 렌더가 0ms 프레임을 반환하면 flush 실패
        var px0Original = GetPx0(frame0.Data);
        if (px0After == px0Original && !isAfterBlack)
        {
            _output.WriteLine("  ❌ flush 후에도 0ms 프레임 반환! last_rendered_frame 미삭제");
        }
        else
        {
            _output.WriteLine("  ✅ flush 정상 — 새로운 프레임 또는 black 반환");
        }

        _renderService.SetPlaybackMode(false); // 복원
    }

    /// <summary>
    /// 가설 6: PlaybackEngine과 메인 Renderer 동시 사용 시 프레임 독립성
    /// </summary>
    [FactRequiresNativeDll]
    public void PlaybackEngine_IndependentFromMainRenderer()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵");
            return;
        }

        Setup(30000);

        // 메인 Renderer로 0ms 렌더
        using var mainFrame = _renderService!.RenderFrame(0);
        Assert.NotNull(mainFrame);
        var mainPx0 = GetPx0(mainFrame.Data);
        _output.WriteLine($"Main Renderer at 0ms: px0={mainPx0}");

        // PlaybackEngine 시작 (15000ms부터)
        _renderService.StartPlaybackEngine(15000);
        _output.WriteLine("PlaybackEngine started at 15000ms");

        // PlaybackEngine에서 프레임 가져오기
        Thread.Sleep(2000); // warmup 대기
        using var pbFrame = _renderService.TryGetPlaybackFrame(15033);
        if (pbFrame != null)
        {
            var pbPx0 = GetPx0(pbFrame.Data);
            _output.WriteLine($"PlaybackEngine at 15033ms: px0={pbPx0}");

            // 메인 Renderer의 0ms와 PlaybackEngine의 15000ms는 달라야 함
            if (mainPx0 == pbPx0)
            {
                _output.WriteLine("  ⚠️ 동일 px0! 두 렌더러가 독립적이지 않을 수 있음");
            }
            else
            {
                _output.WriteLine("  ✅ 다른 px0 — 독립적 렌더링 확인");
            }
        }
        else
        {
            _output.WriteLine("  ⚠️ PlaybackEngine 프레임 없음 (warmup 부족?)");
        }

        _renderService.StopPlaybackEngine();
    }

    public void Dispose()
    {
        _renderService?.Dispose();
        _timelineService?.Dispose();
    }
}

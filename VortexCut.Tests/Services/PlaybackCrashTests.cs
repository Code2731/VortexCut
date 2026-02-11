using VortexCut.Interop.Services;
using VortexCut.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace VortexCut.Tests.Services;

/// <summary>
/// 연속 재생 시뮬레이션 테스트 - ~12초 크래시 재현
/// </summary>
public class PlaybackCrashTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private TimelineService? _timelineService;
    private RenderService? _renderService;

    // 테스트 비디오 경로
    private const string TEST_VIDEO_PATH =
        @"C:\Users\USER\Videos\드론 대응 2.75인치 로켓 '비궁'으로 유도키트 개발, 사우디 기술협력 추진.mp4";

    public PlaybackCrashTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Timeline + Renderer 초기화 헬퍼
    /// </summary>
    private ulong SetupTimelineAndRenderer(long clipDurationMs)
    {
        _timelineService = new TimelineService();
        _renderService = new RenderService();

        _timelineService.CreateTimeline(1920, 1080, 30.0);
        var trackId = _timelineService.AddVideoTrack();
        var clipId = _timelineService.AddVideoClip(trackId, TEST_VIDEO_PATH, 0, clipDurationMs);

        var handle = _timelineService.GetTimelineHandle();
        _renderService.CreateRenderer(handle);

        _output.WriteLine($"✅ Setup: trackId={trackId}, clipId={clipId}, duration={clipDurationMs}ms");
        return clipId;
    }

    /// <summary>
    /// 테스트 1: 단일 프레임 렌더링 (기본 동작 확인)
    /// </summary>
    [FactRequiresNativeDll]
    public void RenderSingleFrame_AtTimestamp0_Success()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵: {TEST_VIDEO_PATH}");
            return;
        }

        SetupTimelineAndRenderer(5000);

        using var frame = _renderService!.RenderFrame(0);
        Assert.NotNull(frame);
        Assert.True(frame.Width > 0);
        Assert.True(frame.Height > 0);
        Assert.True(frame.Data.Length > 0);

        _output.WriteLine($"✅ Frame at 0ms: {frame.Width}x{frame.Height}, {frame.Data.Length} bytes");
    }

    /// <summary>
    /// 테스트 2: 연속 프레임 렌더링 (30fps × 20초 = 600프레임)
    /// 이것이 ~12초 크래시를 재현하는 핵심 테스트
    /// </summary>
    [FactRequiresNativeDll]
    public void ContinuousPlayback_20Seconds_NoCrash()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵: {TEST_VIDEO_PATH}");
            return;
        }

        SetupTimelineAndRenderer(20000);

        const double fps = 30.0;
        const long totalDurationMs = 20000; // 20초
        long frameIntervalMs = (long)(1000.0 / fps); // ~33ms

        int totalFrames = 0;
        int successFrames = 0;
        int nullFrames = 0; // Mutex busy로 스킵
        int errorFrames = 0;
        long lastSuccessTimestamp = 0;
        long firstErrorTimestamp = -1;
        string? firstErrorMessage = null;

        long memoryBefore = GC.GetTotalMemory(true);
        _output.WriteLine($"📊 시작 메모리: {memoryBefore / 1024 / 1024}MB");
        _output.WriteLine($"🎬 연속 재생 시작: 0ms → {totalDurationMs}ms ({fps}fps, ~{totalDurationMs / frameIntervalMs}프레임)");
        _output.WriteLine("---");

        for (long timestampMs = 0; timestampMs < totalDurationMs; timestampMs += frameIntervalMs)
        {
            totalFrames++;

            try
            {
                using var frame = _renderService!.RenderFrame(timestampMs);

                if (frame != null)
                {
                    successFrames++;
                    lastSuccessTimestamp = timestampMs;

                    // 프레임 데이터 무결성 검증
                    Assert.True(frame.Width > 0, $"Width=0 at {timestampMs}ms");
                    Assert.True(frame.Height > 0, $"Height=0 at {timestampMs}ms");
                    long expectedSize = (long)frame.Width * frame.Height * 4;
                    Assert.Equal(expectedSize, frame.Data.Length);
                }
                else
                {
                    nullFrames++;
                }

                // 1초마다 진행 상황 출력
                if (timestampMs % 1000 == 0)
                {
                    long memoryNow = GC.GetTotalMemory(false);
                    _output.WriteLine($"   {timestampMs / 1000}초: 성공={successFrames}, 스킵={nullFrames}, 에러={errorFrames}, 메모리={memoryNow / 1024 / 1024}MB");
                }
            }
            catch (Exception ex)
            {
                errorFrames++;
                if (firstErrorTimestamp == -1)
                {
                    firstErrorTimestamp = timestampMs;
                    firstErrorMessage = ex.ToString();
                }

                _output.WriteLine($"❌ [{timestampMs}ms] 에러: {ex.GetType().Name}: {ex.Message}");

                // 에러 5개 넘으면 중단
                if (errorFrames >= 5)
                {
                    _output.WriteLine($"❌ 에러가 5개 이상 → 테스트 중단");
                    break;
                }
            }
        }

        long memoryAfter = GC.GetTotalMemory(true);

        _output.WriteLine("---");
        _output.WriteLine($"📊 결과:");
        _output.WriteLine($"   총 프레임: {totalFrames}");
        _output.WriteLine($"   성공: {successFrames}");
        _output.WriteLine($"   스킵(null): {nullFrames}");
        _output.WriteLine($"   에러: {errorFrames}");
        _output.WriteLine($"   마지막 성공: {lastSuccessTimestamp}ms ({lastSuccessTimestamp / 1000.0:F1}초)");
        _output.WriteLine($"   메모리: {memoryBefore / 1024 / 1024}MB → {memoryAfter / 1024 / 1024}MB (차이: {(memoryAfter - memoryBefore) / 1024 / 1024}MB)");

        if (firstErrorTimestamp >= 0)
        {
            _output.WriteLine($"   ❌ 첫 에러 시점: {firstErrorTimestamp}ms ({firstErrorTimestamp / 1000.0:F1}초)");
            _output.WriteLine($"   ❌ 에러 내용: {firstErrorMessage}");
        }

        // 에러가 없어야 통과
        Assert.Equal(0, errorFrames);
    }

    /// <summary>
    /// 테스트 3: 고속 렌더링 (프레임 간격 없이 최대 속도로)
    /// 동시성/메모리 문제 조기 발견용
    /// </summary>
    [FactRequiresNativeDll]
    public void RapidFireRendering_500Frames_NoCrash()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵: {TEST_VIDEO_PATH}");
            return;
        }

        SetupTimelineAndRenderer(20000);

        int errorCount = 0;
        long firstErrorMs = -1;
        string? firstError = null;

        _output.WriteLine("🚀 고속 렌더링 테스트: 500프레임, 간격 없음");

        for (int i = 0; i < 500; i++)
        {
            long timestampMs = i * 33L; // ~30fps 시뮬레이션

            try
            {
                using var frame = _renderService!.RenderFrame(timestampMs);
                // frame이 null이면 Mutex busy (정상)
            }
            catch (Exception ex)
            {
                errorCount++;
                if (firstErrorMs == -1)
                {
                    firstErrorMs = timestampMs;
                    firstError = ex.ToString();
                }
                _output.WriteLine($"❌ [{timestampMs}ms] frame #{i}: {ex.GetType().Name}: {ex.Message}");

                if (errorCount >= 3) break;
            }
        }

        _output.WriteLine($"📊 결과: 에러 {errorCount}개" + (firstErrorMs >= 0 ? $", 첫 에러 {firstErrorMs}ms" : ""));
        if (firstError != null)
        {
            _output.WriteLine($"   상세: {firstError}");
        }

        Assert.Equal(0, errorCount);
    }

    /// <summary>
    /// 테스트 4: 10~15초 구간 집중 테스트 (크래시 발생 구간)
    /// </summary>
    [FactRequiresNativeDll]
    public void CriticalRange_10To15Seconds_NoCrash()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵: {TEST_VIDEO_PATH}");
            return;
        }

        SetupTimelineAndRenderer(20000);

        int errorCount = 0;
        long firstErrorMs = -1;
        string? firstError = null;

        _output.WriteLine("🎯 크래시 구간 집중 테스트: 10000ms → 15000ms");

        // 먼저 10초까지 순차적으로 디코딩 (디코더 상태를 쌓아야 함)
        _output.WriteLine("   📦 0~10초 워밍업...");
        for (long ts = 0; ts < 10000; ts += 33)
        {
            try
            {
                using var frame = _renderService!.RenderFrame(ts);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"   ⚠️ 워밍업 에러 [{ts}ms]: {ex.Message}");
            }
        }
        _output.WriteLine("   ✅ 워밍업 완료");

        // 10~15초 구간 세밀하게 테스트
        _output.WriteLine("   🔍 10~15초 구간 상세 테스트...");
        for (long timestampMs = 10000; timestampMs < 15000; timestampMs += 33)
        {
            try
            {
                using var frame = _renderService!.RenderFrame(timestampMs);

                if (frame != null)
                {
                    // 100ms마다 로그
                    if (timestampMs % 100 < 33)
                    {
                        _output.WriteLine($"   ✅ {timestampMs}ms: {frame.Width}x{frame.Height}, {frame.Data.Length} bytes");
                    }
                }
            }
            catch (Exception ex)
            {
                errorCount++;
                if (firstErrorMs == -1)
                {
                    firstErrorMs = timestampMs;
                    firstError = ex.ToString();
                }
                _output.WriteLine($"   ❌ [{timestampMs}ms]: {ex.GetType().Name}: {ex.Message}");

                if (errorCount >= 5) break;
            }
        }

        _output.WriteLine($"📊 결과: 에러 {errorCount}개");
        if (firstError != null)
        {
            _output.WriteLine($"   첫 에러 시점: {firstErrorMs}ms ({firstErrorMs / 1000.0:F1}초)");
            _output.WriteLine($"   상세: {firstError}");
        }

        Assert.Equal(0, errorCount);
    }

    /// <summary>
    /// 테스트 5: 멀티스레드 동시 렌더링 (OnPlaybackTick 시뮬레이션)
    /// Timer.Elapsed는 ThreadPool에서 실행되므로 동시 호출 가능
    /// </summary>
    [FactRequiresNativeDll]
    public async Task ConcurrentRendering_SimulatePlaybackTimer_NoCrash()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵: {TEST_VIDEO_PATH}");
            return;
        }

        SetupTimelineAndRenderer(20000);

        int errorCount = 0;
        long firstErrorMs = -1;
        string? firstError = null;
        int completedFrames = 0;

        _output.WriteLine("🔀 멀티스레드 렌더링 테스트 (Timer.Elapsed 시뮬레이션)");

        var tasks = new List<Task>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 30fps Timer 시뮬레이션: 33ms마다 Task 생성 (최대 15초)
        for (long timestampMs = 0; timestampMs < 15000; timestampMs += 33)
        {
            long ts = timestampMs; // 클로저 캡처

            var task = Task.Run(() =>
            {
                try
                {
                    using var frame = _renderService!.RenderFrame(ts);
                    Interlocked.Increment(ref completedFrames);
                }
                catch (Exception ex)
                {
                    int count = Interlocked.Increment(ref errorCount);
                    if (count == 1)
                    {
                        Interlocked.Exchange(ref firstErrorMs, ts);
                        firstError = ex.ToString();
                    }
                    _output.WriteLine($"❌ [{ts}ms] Thread={Environment.CurrentManagedThreadId}: {ex.GetType().Name}: {ex.Message}");
                }
            }, cts.Token);

            tasks.Add(task);

            // 33ms 간격 시뮬레이션 (실제 Timer와 동일)
            await Task.Delay(10); // 10ms로 빠르게
        }

        await Task.WhenAll(tasks);

        _output.WriteLine($"📊 결과: 완료={completedFrames}, 에러={errorCount}");
        if (firstError != null)
        {
            _output.WriteLine($"   첫 에러: {firstErrorMs}ms");
            _output.WriteLine($"   상세: {firstError}");
        }

        Assert.Equal(0, errorCount);
    }

    /// <summary>
    /// 테스트 6: 메모리 누수 감지 (100프레임 렌더링 후 메모리 증가 확인)
    /// </summary>
    [FactRequiresNativeDll]
    public void MemoryLeak_100Frames_NoExcessiveGrowth()
    {
        if (!File.Exists(TEST_VIDEO_PATH))
        {
            _output.WriteLine($"⚠️ 테스트 비디오 없음, 스킵: {TEST_VIDEO_PATH}");
            return;
        }

        SetupTimelineAndRenderer(10000);

        // 워밍업 (10프레임)
        for (long ts = 0; ts < 330; ts += 33)
        {
            using var frame = _renderService!.RenderFrame(ts);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baselineMemory = GC.GetTotalMemory(true);

        _output.WriteLine($"📊 베이스라인 메모리: {baselineMemory / 1024}KB");

        // 100프레임 렌더링
        for (long ts = 330; ts < 3630; ts += 33)
        {
            using var frame = _renderService!.RenderFrame(ts);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long afterMemory = GC.GetTotalMemory(true);

        long growth = afterMemory - baselineMemory;
        _output.WriteLine($"📊 100프레임 후 메모리: {afterMemory / 1024}KB (증가: {growth / 1024}KB)");

        // 프레임당 ~2MB (960x540x4) = 200MB가 증가하면 누수
        // using으로 dispose하므로 50MB 이하여야 정상
        long maxAllowedGrowthMB = 50;
        Assert.True(growth < maxAllowedGrowthMB * 1024 * 1024,
            $"메모리 누수 의심: {growth / 1024 / 1024}MB 증가 (허용: {maxAllowedGrowthMB}MB)");
    }

    public void Dispose()
    {
        _renderService?.Dispose();
        _timelineService?.Dispose();
    }
}

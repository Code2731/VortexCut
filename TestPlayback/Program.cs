using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Linq;
using VortexCut.Interop.Services;

// 파일 로그
var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "playback_test.log");
using var logWriter = new StreamWriter(logPath, false) { AutoFlush = true };

void Log(string msg)
{
    var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
    logWriter.WriteLine(line);
    Console.Out.Flush();
}

Log("=== 테스트 1: 순차 렌더링 (이미 통과) ===");
Log("=== 테스트 2: Timer 기반 멀티스레드 재생 시뮬레이션 ===");

var videoPath = @"C:\Users\USER\Videos\드론 대응 2.75인치 로켓 '비궁'으로 유도키트 개발, 사우디 기술협력 추진.mp4";

if (!File.Exists(videoPath))
{
    Log($"비디오 없음: {videoPath}");
    Environment.Exit(1);
}

TimelineService? timelineService = null;
RenderService? renderService = null;

try
{
    // 초기화
    Log("[1] Timeline + Renderer 초기화...");
    timelineService = new TimelineService();
    timelineService.CreateTimeline(1920, 1080, 30.0);
    var trackId = timelineService.AddVideoTrack();
    timelineService.AddVideoClip(trackId, videoPath, 0, 20000);
    renderService = new RenderService();
    renderService.CreateRenderer(timelineService.GetTimelineHandle());
    Log("    초기화 완료");

    // ============ 멀티스레드 Timer 시뮬레이션 ============
    // PreviewViewModel.OnPlaybackTick과 동일 패턴:
    // - System.Timers.Timer (33ms 간격)
    // - Timer.Elapsed는 ThreadPool에서 실행
    // - RenderFrame을 Task.Run으로 호출
    // - volatile _isRendering으로 동시 실행 방지

    long currentTimeMs = 0;
    int successFrames = 0;
    int nullFrames = 0;
    int errorFrames = 0;
    int isRenderingFlag = 0; // 0=false, 1=true (Interlocked용)
    var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
    var sw = Stopwatch.StartNew();
    long memBefore = GC.GetTotalMemory(true);

    Log("[2] Timer 기반 재생 시작 (30fps, 20초)...");

    var timer = new System.Timers.Timer(1000.0 / 30.0); // 33ms
    timer.Elapsed += (sender, e) =>
    {
        var newTimeMs = Interlocked.Add(ref currentTimeMs, 33);

        // 20초 넘으면 정지
        if (newTimeMs >= 20000)
        {
            timer.Stop();
            return;
        }

        // 렌더링 동시성 제어 (PreviewViewModel과 동일)
        if (Interlocked.CompareExchange(ref isRenderingFlag, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // isRenderingFlag already set to 1 by CompareExchange

                // RenderFrameAsync 시뮬레이션
                byte[]? frameData = null;
                uint width = 0, height = 0;

                await Task.Run(() =>
                {
                    using var frame = renderService!.RenderFrame(newTimeMs);
                    if (frame != null)
                    {
                        // 데이터 복사 (PreviewViewModel과 동일)
                        frameData = frame.Data.ToArray();
                        width = frame.Width;
                        height = frame.Height;
                    }
                });

                if (frameData != null)
                {
                    Interlocked.Increment(ref successFrames);

                    // 1초마다 로그
                    if (newTimeMs % 1000 < 33)
                    {
                        long memNow = GC.GetTotalMemory(false);
                        Log($"    {newTimeMs / 1000}s: ok={successFrames} skip={nullFrames} err={errorFrames} mem={memNow / 1024 / 1024}MB");
                    }
                }
                else
                {
                    Interlocked.Increment(ref nullFrames);
                }
            }
            catch (Exception ex)
            {
                int count = Interlocked.Increment(ref errorFrames);
                var msg = $"[{newTimeMs}ms] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                errors.Add(msg);
                Log($"    ERROR {msg}");

                if (count >= 5)
                {
                    timer.Stop();
                }
            }
            finally
            {
                Interlocked.Exchange(ref isRenderingFlag, 0);
            }
        });
    };

    timer.Start();

    // 타이머가 끝날 때까지 대기 (최대 30초)
    var deadline = DateTime.Now.AddSeconds(30);
    while (timer.Enabled && DateTime.Now < deadline)
    {
        Thread.Sleep(100);
    }
    timer.Stop();
    timer.Dispose();

    // 남은 Task 대기
    Thread.Sleep(500);

    sw.Stop();
    long memAfter = GC.GetTotalMemory(true);

    Log("========== 결과 ==========");
    Log($"    도달 시간: {Interlocked.Read(ref currentTimeMs)}ms");
    Log($"    성공: {successFrames}");
    Log($"    스킵(null): {nullFrames}");
    Log($"    에러: {errorFrames}");
    Log($"    소요: {sw.Elapsed.TotalSeconds:F1}s");
    Log($"    메모리: {memBefore / 1024 / 1024}MB -> {memAfter / 1024 / 1024}MB");

    if (errorFrames > 0)
    {
        Log("에러 목록:");
        foreach (var err in errors)
        {
            Log($"    {err}");
        }
        Log($"FAIL - {errorFrames}개 에러");
        Environment.Exit(1);
    }
    else
    {
        Log("PASS - Timer 기반 재생 크래시 없음!");
    }
}
catch (Exception ex)
{
    Log($"CRASH: {ex.GetType().Name}: {ex.Message}");
    Log($"Stack: {ex.StackTrace}");
    Environment.Exit(1);
}
finally
{
    renderService?.Dispose();
    timelineService?.Dispose();
}

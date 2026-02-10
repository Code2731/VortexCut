using System;
using System.IO;
using System.Threading;
using VortexCut.UI.ViewModels;
using VortexCut.UI.Services;

Console.WriteLine("=== VortexCut Playback Test ===\n");

try
{
    // 1. ProjectService 초기화
    Console.WriteLine("[1/5] Initializing ProjectService...");
    var projectService = new ProjectService();
    projectService.CreateProject("Test", 1920, 1080, 30.0);
    Console.WriteLine("OK - Project created\n");

    // 2. 테스트 비디오 추가 (사용자가 로드한 파일)
    var videoPath = @"C:\Users\USER\Videos\test.mp4";

    // 실제 파일이 없으면 더미 클립 추가
    Console.WriteLine($"[2/5] Adding video clip...");
    if (File.Exists(videoPath))
    {
        projectService.AddVideoClip(videoPath, 0, 5000, 0);
        Console.WriteLine($"OK - Video loaded: {videoPath}\n");
    }
    else
    {
        Console.WriteLine($"WARN - Test video not found, using dummy clip\n");
        // 더미 클립 추가 (렌더링은 실패할 수 있음)
        projectService.AddVideoClip("dummy.mp4", 0, 5000, 0);
    }

    // 3. PreviewViewModel 생성
    Console.WriteLine("[3/5] Creating PreviewViewModel...");
    var preview = new PreviewViewModel(projectService);
    Console.WriteLine("OK - PreviewViewModel created\n");

    // 4. 단일 프레임 렌더링 테스트
    Console.WriteLine("[4/5] Testing single frame render...");
    try
    {
        var frame = projectService.RenderFrame(0);
        Console.WriteLine($"OK - Frame rendered: {frame.Width}x{frame.Height}, {frame.Data.Length} bytes\n");
        frame.Dispose();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR - Frame render failed: {ex.Message}\n");
    }

    // 5. 재생 토글 테스트
    Console.WriteLine("[5/5] Testing playback toggle...");

    Console.WriteLine("  - Starting playback...");
    preview.TogglePlayback();

    Thread.Sleep(2000); // 2초 재생

    Console.WriteLine($"  - Current time: {preview.CurrentTimeMs}ms");
    Console.WriteLine("  - Stopping playback...");
    preview.TogglePlayback();

    Console.WriteLine($"  - Final time: {preview.CurrentTimeMs}ms\n");

    // 검증
    if (preview.CurrentTimeMs > 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=== TEST PASSED ===");
        Console.WriteLine($"Playback worked! Time advanced to {preview.CurrentTimeMs}ms");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("=== TEST FAILED ===");
        Console.WriteLine("Playback did not advance time");
        Console.ResetColor();
        Environment.Exit(1);
    }

    // Cleanup
    preview.Dispose();
    projectService.Dispose();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n=== TEST CRASHED ===");
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Stack: {ex.StackTrace}");
    Console.ResetColor();
    Environment.Exit(1);
}

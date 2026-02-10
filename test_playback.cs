// 간단한 재생 로직 검증 테스트
using VortexCut.UI.ViewModels;
using VortexCut.UI.Services;

var projectService = new ProjectService();
projectService.CreateProject("Test", 1920, 1080, 30.0);

// 테스트 비디오 클립 추가 (사용자가 로드한 파일 경로)
var testVideoPath = @"C:\Users\USER\Videos\test.mp4"; // 경로 수정 필요

if (File.Exists(testVideoPath))
{
    projectService.AddVideoClip(testVideoPath, 0, 5000, 0);

    var preview = new PreviewViewModel(projectService);

    // 프레임 렌더링 테스트
    Console.WriteLine("Testing frame rendering at 0ms...");
    var frame = projectService.RenderFrame(0);
    Console.WriteLine($"✅ Frame rendered: {frame.Width}x{frame.Height}, {frame.Data.Length} bytes");

    // 재생 토글 테스트
    Console.WriteLine("\nTesting playback toggle...");
    preview.TogglePlayback();
    Console.WriteLine("✅ Playback started");

    Thread.Sleep(1000); // 1초 대기

    preview.TogglePlayback();
    Console.WriteLine("✅ Playback stopped");
    Console.WriteLine($"Final time: {preview.CurrentTimeMs}ms");
}
else
{
    Console.WriteLine($"❌ Test video not found: {testVideoPath}");
}

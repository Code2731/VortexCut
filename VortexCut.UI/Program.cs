using Avalonia;
using System;

namespace VortexCut.UI;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // GPU 하드웨어 가속 렌더링 (ANGLE Direct3D → 소프트웨어 폴백)
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
                // WinUI Composition: DirectComposition 기반 부드러운 합성 (Win10 1803+)
                CompositionMode = new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface }
            })
            .WithInterFont()
            .LogToTrace();
}

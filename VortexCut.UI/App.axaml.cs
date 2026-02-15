using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VortexCut.Core.Interfaces;
using VortexCut.Interop.Services;
using VortexCut.UI.Services;
using VortexCut.UI.ViewModels;
using VortexCut.UI.Views;

namespace VortexCut.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // 디버그 로거 초기화
        DebugLogger.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Composition Root — DI 컨테이너 구성
        var services = new ServiceCollection();

        // Interop 서비스
        services.AddSingleton<TimelineService>();
        services.AddSingleton<IRenderService, RenderService>();

        // UI 서비스
        services.AddSingleton<ProjectService>();
        services.AddSingleton<IProjectService>(sp => sp.GetRequiredService<ProjectService>());
        services.AddSingleton<ProjectSerializationService>();
        services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();
        services.AddSingleton<ProxyService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        var provider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = provider.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow(mainVm);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

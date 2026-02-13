using System;
using System.Timers;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Interop.Services;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// Export 프리셋
/// </summary>
public record ExportPreset(string Name, uint Width, uint Height, double Fps, uint Crf);

/// <summary>
/// Export 다이얼로그 ViewModel
/// </summary>
public partial class ExportViewModel : ViewModelBase, IDisposable
{
    private readonly ProjectService _projectService;
    private readonly ExportService _exportService;
    private readonly System.Timers.Timer _progressTimer;

    // === 설정 ===

    [ObservableProperty]
    private uint _width = 1920;

    [ObservableProperty]
    private uint _height = 1080;

    [ObservableProperty]
    private double _fps = 30.0;

    [ObservableProperty]
    private uint _crf = 23;

    [ObservableProperty]
    private string _outputPath = "";

    [ObservableProperty]
    private int _selectedPresetIndex = 1; // 기본: 1080p 표준

    // === 진행 상태 ===

    [ObservableProperty]
    private int _progress = 0;

    [ObservableProperty]
    private bool _isExporting = false;

    [ObservableProperty]
    private string _statusText = "준비";

    [ObservableProperty]
    private bool _isComplete = false;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Export 프리셋 목록
    /// </summary>
    public static ExportPreset[] Presets { get; } = new[]
    {
        new ExportPreset("1080p 고품질", 1920, 1080, 30, 18),
        new ExportPreset("1080p 표준", 1920, 1080, 30, 23),
        new ExportPreset("720p 빠른", 1280, 720, 30, 28),
        new ExportPreset("4K UHD", 3840, 2160, 30, 20),
    };

    /// <summary>
    /// Export 완료 시 호출되는 콜백 (다이얼로그 닫기용)
    /// </summary>
    public Action? OnExportComplete { get; set; }

    public ExportViewModel(ProjectService projectService)
    {
        _projectService = projectService;
        _exportService = new ExportService();

        // 진행률 폴링 타이머 (500ms 간격)
        _progressTimer = new System.Timers.Timer(500);
        _progressTimer.Elapsed += OnProgressTimerTick;

        // 기본 프리셋 적용
        ApplyPreset(Presets[1]);
    }

    /// <summary>
    /// 프리셋 선택 시 설정 자동 적용
    /// </summary>
    partial void OnSelectedPresetIndexChanged(int value)
    {
        if (value >= 0 && value < Presets.Length)
        {
            ApplyPreset(Presets[value]);
        }
    }

    private void ApplyPreset(ExportPreset preset)
    {
        Width = preset.Width;
        Height = preset.Height;
        Fps = preset.Fps;
        Crf = preset.Crf;
    }

    /// <summary>
    /// Export 시작
    /// </summary>
    [RelayCommand]
    private void StartExport()
    {
        if (IsExporting) return;

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            StatusText = "출력 경로를 지정해주세요";
            return;
        }

        var timelineHandle = _projectService.TimelineRawHandle;
        if (timelineHandle == IntPtr.Zero)
        {
            StatusText = "프로젝트가 열려있지 않습니다";
            return;
        }

        try
        {
            _exportService.StartExport(
                timelineHandle,
                OutputPath,
                Width,
                Height,
                Fps,
                Crf);

            IsExporting = true;
            IsComplete = false;
            ErrorMessage = null;
            Progress = 0;
            StatusText = "Export 진행 중...";

            // 진행률 폴링 시작
            _progressTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"Export 시작 실패: {ex.Message}";
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Export 취소
    /// </summary>
    [RelayCommand]
    private void CancelExport()
    {
        if (!IsExporting) return;

        _exportService.Cancel();
        StatusText = "취소 중...";
    }

    /// <summary>
    /// 진행률 폴링 타이머
    /// </summary>
    private void OnProgressTimerTick(object? sender, ElapsedEventArgs e)
    {
        var progress = _exportService.GetProgress();
        var finished = _exportService.IsFinished();
        var error = _exportService.GetError();

        Dispatcher.UIThread.Post(() =>
        {
            Progress = progress;

            if (finished)
            {
                _progressTimer.Stop();
                IsExporting = false;
                IsComplete = true;

                if (error != null)
                {
                    ErrorMessage = error;
                    StatusText = $"Export 실패: {error}";
                }
                else
                {
                    StatusText = "Export 완료!";
                }

                // 리소스 정리
                _exportService.Cleanup();

                OnExportComplete?.Invoke();
            }
            else
            {
                StatusText = $"Export 진행 중... {progress}%";
            }
        });
    }

    public void Dispose()
    {
        _progressTimer?.Stop();
        _progressTimer?.Dispose();
        _exportService?.Dispose();
        GC.SuppressFinalize(this);
    }
}

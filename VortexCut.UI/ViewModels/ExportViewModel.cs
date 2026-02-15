using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Timers;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Models;
using VortexCut.Core.Interfaces;
using VortexCut.Interop.Services;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// Export 프리셋
/// </summary>
public record ExportPreset(string Name, uint Width, uint Height, double Fps, uint Crf);

/// <summary>
/// 인코더 옵션 (ComboBox 표시용)
/// </summary>
public record EncoderOption(string Name, uint EncoderType);

/// <summary>
/// Export 다이얼로그 ViewModel
/// </summary>
public partial class ExportViewModel : ViewModelBase, IDisposable
{
    private readonly IProjectService _projectService;
    private readonly IExportService _exportService;
    private readonly System.Timers.Timer _progressTimer;
    private TimelineViewModel? _timelineViewModel;

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

    [ObservableProperty]
    private int _selectedEncoderIndex = 0; // 기본: 자동

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
    /// 사용 가능한 인코더 목록 (시작 시 탐지)
    /// </summary>
    public List<EncoderOption> AvailableEncoders { get; } = new();

    /// <summary>
    /// Export 완료 시 호출되는 콜백 (다이얼로그 닫기용)
    /// </summary>
    public Action? OnExportComplete { get; set; }

    /// <summary>
    /// TimelineViewModel 설정 (자막 클립 접근용)
    /// </summary>
    public void SetTimelineViewModel(TimelineViewModel timelineVm)
    {
        _timelineViewModel = timelineVm;
    }

    public ExportViewModel(IProjectService projectService)
    {
        _projectService = projectService;
        _exportService = new ExportService();

        // 진행률 폴링 타이머 (500ms 간격)
        _progressTimer = new System.Timers.Timer(500);
        _progressTimer.Elapsed += OnProgressTimerTick;

        // 기본 프리셋 적용
        ApplyPreset(Presets[1]);

        // 사용 가능한 인코더 탐지
        DetectEncoders();
    }

    /// <summary>
    /// 사용 가능한 인코더 탐지하여 목록 구성
    /// </summary>
    private void DetectEncoders()
    {
        AvailableEncoders.Clear();
        AvailableEncoders.Add(new EncoderOption("자동 감지", 0));

        try
        {
            uint mask = ExportService.DetectEncoders();

            if ((mask & 1) != 0)
                AvailableEncoders.Add(new EncoderOption("CPU (libx264)", 1));
            if ((mask & 2) != 0)
                AvailableEncoders.Add(new EncoderOption("NVIDIA NVENC", 2));
            if ((mask & 4) != 0)
                AvailableEncoders.Add(new EncoderOption("Intel QSV", 3));
            if ((mask & 8) != 0)
                AvailableEncoders.Add(new EncoderOption("AMD AMF", 4));
        }
        catch
        {
            // DLL 로딩 실패 시 소프트웨어만 추가
            AvailableEncoders.Add(new EncoderOption("CPU (libx264)", 1));
        }

        SelectedEncoderIndex = 0;
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
            // 선택된 인코더 타입 가져오기
            uint encoderType = 0; // Auto
            if (SelectedEncoderIndex >= 0 && SelectedEncoderIndex < AvailableEncoders.Count)
            {
                encoderType = AvailableEncoders[SelectedEncoderIndex].EncoderType;
            }

            // 자막 오버레이 생성 (SubtitleClipModel이 있으면)
            var subtitleListHandle = BuildSubtitleOverlays();

            // v3 API: 인코더 타입 + 자막 (자막 없으면 IntPtr.Zero)
            _exportService.StartExportV3(
                timelineHandle,
                OutputPath,
                Width,
                Height,
                Fps,
                Crf,
                encoderType,
                subtitleListHandle);

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

    /// <summary>
    /// 자막 클립 → Rust SubtitleOverlayList 생성
    /// 자막 클립이 없으면 IntPtr.Zero 반환
    /// </summary>
    private IntPtr BuildSubtitleOverlays()
    {
        if (_timelineViewModel == null) return IntPtr.Zero;

        var subtitleClips = _timelineViewModel.Clips
            .OfType<SubtitleClipModel>()
            .OrderBy(c => c.StartTimeMs)
            .ToList();

        if (subtitleClips.Count == 0) return IntPtr.Zero;

        var listHandle = _exportService.CreateSubtitleList();
        if (listHandle == IntPtr.Zero) return IntPtr.Zero;

        int videoWidth = (int)Width;
        int videoHeight = (int)Height;

        foreach (var clip in subtitleClips)
        {
            try
            {
                // Avalonia로 자막 텍스트 → RGBA 비트맵 렌더링
                var bitmap = SubtitleRenderService.RenderSubtitle(
                    clip.Text, clip.Style, videoWidth, videoHeight);

                // RGBA 데이터를 네이티브 메모리에 복사하여 FFI 전달
                IntPtr rgbaPtr = Marshal.AllocCoTaskMem(bitmap.RgbaData.Length);
                try
                {
                    Marshal.Copy(bitmap.RgbaData, 0, rgbaPtr, bitmap.RgbaData.Length);

                    _exportService.SubtitleListAdd(
                        listHandle,
                        clip.StartTimeMs,
                        clip.EndTimeMs,
                        bitmap.X,
                        bitmap.Y,
                        (uint)bitmap.Width,
                        (uint)bitmap.Height,
                        rgbaPtr,
                        (uint)bitmap.RgbaData.Length);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(rgbaPtr);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"자막 오버레이 생성 실패 [{clip.StartTimeMs}ms]: {ex.Message}");
            }
        }

        return listHandle;
    }

    public void Dispose()
    {
        _progressTimer?.Stop();
        _progressTimer?.Dispose();
        _exportService?.Dispose();
        GC.SuppressFinalize(this);
    }
}

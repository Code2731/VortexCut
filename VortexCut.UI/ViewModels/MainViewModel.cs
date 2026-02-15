using System;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using VortexCut.Core.Interfaces;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// 메인 윈도우 ViewModel - Kdenlive 스타일
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ProjectService _projectService;
    private readonly ProxyService _proxyService;
    private IStorageProvider? _storageProvider;
    private ToastService? _toastService;
    private bool _isInitialized = false;
    private int _projectCounter = 0;

    /// <summary>
    /// Export 다이얼로그 열기 요청 (MainWindow에서 핸들링)
    /// </summary>
    public Action? RequestOpenExportDialog { get; set; }

    /// <summary>
    /// ProjectService 접근 (Export에서 사용)
    /// </summary>
    public ProjectService ProjectService => _projectService;

    [ObservableProperty]
    private ProjectBinViewModel _projectBin;

    [ObservableProperty]
    private TimelineViewModel _timeline;

    [ObservableProperty]
    private PreviewViewModel _preview;

    [ObservableProperty]
    private SourceMonitorViewModel _sourceMonitor;

    [ObservableProperty]
    private string _projectName = "Untitled Project";

    /// <summary>
    /// Inspector 패널 ViewModel (색보정, 오디오, 트랜지션 비즈니스 로직)
    /// </summary>
    public InspectorViewModel Inspector { get; }

    public MainViewModel(ProjectService projectService, IAudioPlaybackService audioPlayback)
    {
        _projectService = projectService;
        _proxyService = new ProxyService();
        _projectBin = new ProjectBinViewModel();
        _timeline = new TimelineViewModel(_projectService);
        _preview = new PreviewViewModel(_projectService, audioPlayback);
        _sourceMonitor = new SourceMonitorViewModel();
        Inspector = new InspectorViewModel(_projectService, _preview, _timeline);

        // Preview와 Timeline 연결
        _preview.SetTimelineViewModel(_timeline);

        // 타임라인에서 재생 중지 요청 시 처리 (무조건 중지)
        _timeline.RequestStopPlayback = () =>
        {
            if (_preview.IsPlaying)
            {
                _preview.TogglePlayback();
            }
            _timeline.IsPlaying = false;
        };
    }

    /// <summary>
    /// 초기화 (Window Opened에서 한 번만 호출)
    /// </summary>
    public void Initialize()
    {
        if (!_isInitialized)
        {
            CreateNewProject();
            _isInitialized = true;
        }
    }

    /// <summary>
    /// StorageProvider 설정 (Window에서 호출)
    /// </summary>
    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    /// <summary>
    /// ToastService 설정 (Window에서 호출)
    /// </summary>
    public void SetToastService(ToastService toastService)
    {
        _toastService = toastService;
    }

}

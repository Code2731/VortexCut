using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Models;
using VortexCut.Core.Serialization;
using VortexCut.UI.Services;
using VortexCut.Interop.Services;
using Avalonia.Media.Imaging;

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
    private string _projectName = "Untitled Project";

    public MainViewModel()
    {
        _projectService = new ProjectService();
        _proxyService = new ProxyService();
        _projectBin = new ProjectBinViewModel();
        _timeline = new TimelineViewModel(_projectService);
        _preview = new PreviewViewModel(_projectService);

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

    [RelayCommand]
    private void CreateNewProject()
    {
        try
        {
            // 카운터 증가
            _projectCounter++;

            var projectName = $"New Project #{_projectCounter}";

            _projectService.CreateProject(projectName, 1920, 1080, 30.0);
            ProjectName = projectName;

            Timeline.Reset();
            Preview.Reset();
            ProjectBin.Clear();

            _toastService?.ShowSuccess("프로젝트 생성 완료", $"{projectName}이(가) 생성되었습니다.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CreateNewProject ERROR: {ex.Message}");
            _toastService?.ShowError("프로젝트 생성 실패", ex.Message);
            throw;
        }
    }

    [RelayCommand]
    private async Task OpenVideoFileAsync()
    {
        if (_storageProvider == null)
        {
            _toastService?.ShowError("오류", "파일 선택기를 사용할 수 없습니다.");
            return;
        }

        try
        {
            var fileTypes = new FilePickerFileType[]
            {
                new("Video Files")
                {
                    Patterns = new[] { "*.mp4", "*.avi", "*.mkv", "*.mov", "*.wmv", "*.flv", "*.webm" }
                },
                new("All Files")
                {
                    Patterns = new[] { "*.*" }
                }
            };

            var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "비디오 파일 가져오기",
                FileTypeFilter = fileTypes,
                AllowMultiple = true
            });

            if (files.Count == 0)
                return;

            // Loading 상태 시작
            ProjectBin.SetLoading(true);

            int fileIndex = 0;
            foreach (var file in files)
            {
                var filePath = file.Path.LocalPath;
                var fileName = Path.GetFileName(filePath);

                // 썸네일 생성 및 저장 (0ms 한 장, 세션 엔진과는 독립적인 간단 경로)
                string? thumbnailPath = null;
                try
                {
                    thumbnailPath = await Task.Run(() => GenerateThumbnailForFile(filePath));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"⚠️ Failed to generate thumbnail for {fileName}: {ex.Message}");
                }

                // 비디오 정보 조회 (Rust FFI)
                long durationMs = 5000;
                uint width = 1920, height = 1080;
                double fps = 30;
                try
                {
                    var videoInfo = await Task.Run(() => RenderService.GetVideoInfo(filePath));
                    durationMs = videoInfo.DurationMs > 0 ? videoInfo.DurationMs : 5000;
                    width = videoInfo.Width;
                    height = videoInfo.Height;
                    fps = videoInfo.Fps;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"GetVideoInfo failed for {fileName}: {ex.Message}");
                }

                // Proxy 비디오 생성 (실패 시 null, 원본만 사용)
                string? proxyPath = null;
                try
                {
                    proxyPath = await _proxyService.CreateProxyAsync(filePath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ProxyService failed for {fileName}: {ex.Message}");
                }

                // Project Bin에 추가
                var mediaItem = new MediaItem
                {
                    Name = fileName,
                    FilePath = filePath,
                    Type = MediaType.Video,
                    DurationMs = durationMs,
                    Width = width,
                    Height = height,
                    Fps = fps,
                    ThumbnailPath = thumbnailPath,
                    ProxyFilePath = proxyPath
                };

                ProjectBin.AddMediaItem(mediaItem);

                // 첫 번째 파일은 타임라인에도 자동 추가
                if (fileIndex == 0)
                {
                    await Timeline.AddVideoClipAsync(filePath, proxyPath);
                    Preview.RenderFrameAsync(0);
                }

                fileIndex++;
            }

            // Loading 상태 종료
            ProjectBin.SetLoading(false);

            _toastService?.ShowSuccess("미디어 임포트 완료", $"{files.Count}개의 파일을 추가했습니다.");
        }
        catch (Exception ex)
        {
            ProjectBin.SetLoading(false);
            System.Diagnostics.Debug.WriteLine($"OpenVideoFileAsync ERROR: {ex}");
            _toastService?.ShowError("미디어 임포트 실패", ex.Message);
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        try
        {
            Preview.TogglePlayback();
            Timeline.IsPlaying = Preview.IsPlaying; // UI 상태 동기화
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PlayPause ERROR: {ex.Message}");
            _toastService?.ShowError("재생 오류", "비디오를 재생할 수 없습니다. 파일을 확인해주세요.");
        }
    }

    [RelayCommand]
    private void Export()
    {
        // 클립이 없으면 Export 불가
        if (Timeline.Clips.Count == 0)
        {
            _toastService?.ShowError("Export 불가", "타임라인에 클립이 없습니다.");
            return;
        }

        // MainWindow에서 다이얼로그 열기
        RequestOpenExportDialog?.Invoke();
    }

    /// <summary>
    /// 비디오 파일의 썸네일 생성 및 저장 (플랫폼별 경로 처리)
    /// </summary>
    private string GenerateThumbnailForFile(string videoFilePath)
    {
        // 플랫폼별 썸네일 디렉토리 설정
        string thumbnailDir;
        if (OperatingSystem.IsWindows())
        {
            thumbnailDir = Path.Combine(Path.GetTempPath(), "vortexcut_thumbnails");
        }
        else if (OperatingSystem.IsMacOS())
        {
            thumbnailDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches", "vortexcut_thumbnails");
        }
        else // Linux
        {
            thumbnailDir = Path.Combine("/tmp", "vortexcut_thumbnails");
        }

        // 디렉토리 생성
        Directory.CreateDirectory(thumbnailDir);

        // 썸네일 파일 이름 (비디오 파일 이름 기반)
        var videoFileName = Path.GetFileNameWithoutExtension(videoFilePath);
        var thumbnailFileName = $"{videoFileName}_{Guid.NewGuid():N}.png";
        var thumbnailPath = Path.Combine(thumbnailDir, thumbnailFileName);

        System.Diagnostics.Debug.WriteLine($"📸 Generating thumbnail: {videoFilePath} -> {thumbnailPath}");

        // Rust에서 썸네일 생성 (160x90)
        using var thumbnailFrame = RenderService.GenerateThumbnail(videoFilePath, 0, 160, 90);

        // WriteableBitmap으로 변환
        var bitmap = new WriteableBitmap(
            new Avalonia.PixelSize((int)thumbnailFrame.Width, (int)thumbnailFrame.Height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888,
            Avalonia.Platform.AlphaFormat.Unpremul
        );

        using (var buffer = bitmap.Lock())
        {
            unsafe
            {
                fixed (byte* srcPtr = thumbnailFrame.Data)
                {
                    var dst = (byte*)buffer.Address;
                    var size = (int)thumbnailFrame.Width * (int)thumbnailFrame.Height * 4;
                    Buffer.MemoryCopy(srcPtr, dst, size, size);
                }
            }
        }

        // PNG 파일로 저장
        using (var fileStream = File.Create(thumbnailPath))
        {
            bitmap.Save(fileStream);
        }

        System.Diagnostics.Debug.WriteLine($"✅ Thumbnail saved: {thumbnailPath}");

        return thumbnailPath;
    }

    [RelayCommand]
    private async Task SaveProject()
    {
        if (_storageProvider == null)
        {
            _toastService?.ShowError("오류", "파일 선택기를 사용할 수 없습니다.");
            return;
        }

        try
        {
            var data = _projectService.ExtractProjectData(this);

            // 이미 저장된 경로가 있으면 덮어쓰기, 없으면 Save As 동작
            var currentProject = _projectService.CurrentProject;
            string? filePath = currentProject?.FilePath;

            if (string.IsNullOrEmpty(filePath))
            {
                filePath = await ShowSaveDialog(".vortex");
            }

            if (string.IsNullOrEmpty(filePath))
                return;

            await ProjectSerializer.SaveToFileAsync(data, filePath);

            if (currentProject != null)
            {
                currentProject.FilePath = filePath;
                currentProject.Name = data.ProjectName;
            }

            _toastService?.ShowSuccess("프로젝트 저장 완료", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveProject ERROR: {ex}");
            _toastService?.ShowError("프로젝트 저장 실패", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveProjectAs()
    {
        if (_storageProvider == null)
        {
            _toastService?.ShowError("오류", "파일 선택기를 사용할 수 없습니다.");
            return;
        }

        try
        {
            var data = _projectService.ExtractProjectData(this);
            var filePath = await ShowSaveDialog(".vortex");
            if (string.IsNullOrEmpty(filePath))
                return;

            await ProjectSerializer.SaveToFileAsync(data, filePath);

            var currentProject = _projectService.CurrentProject;
            if (currentProject != null)
            {
                currentProject.FilePath = filePath;
                currentProject.Name = data.ProjectName;
            }

            _toastService?.ShowSuccess("프로젝트 저장 완료", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveProjectAs ERROR: {ex}");
            _toastService?.ShowError("프로젝트 저장 실패", ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadProject()
    {
        if (_storageProvider == null)
        {
            _toastService?.ShowError("오류", "파일 선택기를 사용할 수 없습니다.");
            return;
        }

        try
        {
            var filePath = await ShowOpenDialog(".vortex");
            if (string.IsNullOrEmpty(filePath))
                return;

            var data = await ProjectSerializer.LoadFromFileAsync(filePath);
            if (data == null)
            {
                _toastService?.ShowError("프로젝트 불러오기 실패", "프로젝트 파일을 읽을 수 없습니다.");
                return;
            }

            _projectService.RestoreProjectData(data, this);

            var currentProject = _projectService.CurrentProject;
            if (currentProject != null)
            {
                currentProject.FilePath = filePath;
                currentProject.Name = data.ProjectName;
            }

            ProjectName = data.ProjectName;

            _toastService?.ShowSuccess("프로젝트 불러오기 완료", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadProject ERROR: {ex}");
            _toastService?.ShowError("프로젝트 불러오기 실패", ex.Message);
        }
    }

    private async Task<string?> ShowSaveDialog(string defaultExtension)
    {
        if (_storageProvider == null)
            return null;

        var fileTypes = new[]
        {
            new FilePickerFileType("Vortex Project")
            {
                Patterns = new[] { "*.vortex" }
            }
        };

        var result = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "프로젝트 저장",
            DefaultExtension = defaultExtension.TrimStart('.'),
            FileTypeChoices = fileTypes,
            SuggestedFileName = Path.ChangeExtension(ProjectName, defaultExtension)
        });

        return result?.Path.LocalPath;
    }

    private async Task<string?> ShowOpenDialog(string extension)
    {
        if (_storageProvider == null)
            return null;

        var fileTypes = new[]
        {
            new FilePickerFileType("Vortex Project")
            {
                Patterns = new[] { "*.vortex" }
            }
        };

        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "프로젝트 열기",
            FileTypeFilter = fileTypes,
            AllowMultiple = false
        });

        var file = files.FirstOrDefault();
        return file?.Path.LocalPath;
    }
}

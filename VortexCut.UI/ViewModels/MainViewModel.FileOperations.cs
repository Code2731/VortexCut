using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Models;
using VortexCut.Core.Serialization;
using Avalonia.Media.Imaging;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// MainViewModel — 파일 I/O 관련 커맨드 (Open, Save, Load, Import)
/// </summary>
public partial class MainViewModel
{
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
                    System.Diagnostics.Debug.WriteLine($"Failed to generate thumbnail for {fileName}: {ex.Message}");
                }

                // 비디오 정보 조회 (Rust FFI)
                long durationMs = 5000;
                uint width = 1920, height = 1080;
                double fps = 30;
                try
                {
                    var videoInfo = await Task.Run(() => _projectService.GetVideoInfo(filePath));
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

                // 타임라인이 비어있고 첫 번째 파일일 때만 자동 추가
                if (fileIndex == 0 && Timeline.Clips.Count == 0)
                {
                    try
                    {
                        await Timeline.AddVideoClipAsync(filePath, proxyPath);
                        Preview.RenderFrameAsync(0);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Timeline add failed for {fileName}: {ex.Message}");
                    }
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
    private async Task ImportSrtFileAsync()
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
                new("SRT Subtitle Files")
                {
                    Patterns = new[] { "*.srt" }
                },
                new("All Files")
                {
                    Patterns = new[] { "*.*" }
                }
            };

            var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "SRT 자막 파일 가져오기",
                FileTypeFilter = fileTypes,
                AllowMultiple = false
            });

            if (files.Count == 0)
                return;

            var filePath = files[0].Path.LocalPath;

            // 자막 트랙이 없으면 기본 트랙 생성
            if (Timeline.SubtitleTracks.Count == 0)
            {
                Timeline.AddSubtitleTrackCommand.Execute(null);
            }

            Timeline.ImportSrt(filePath, 0);

            _toastService?.ShowSuccess("자막 임포트 완료", $"{Path.GetFileName(filePath)}을(를) 가져왔습니다.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ImportSrtFileAsync ERROR: {ex}");
            _toastService?.ShowError("자막 임포트 실패", ex.Message);
        }
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
            var data = _serializationService.ExtractProjectData(this);

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
            var data = _serializationService.ExtractProjectData(this);
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

            _serializationService.RestoreProjectData(data, this);

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

        System.Diagnostics.Debug.WriteLine($"Generating thumbnail: {videoFilePath} -> {thumbnailPath}");

        // Rust에서 썸네일 생성 (160x90)
        using var thumbnailFrame = _projectService.GenerateThumbnail(videoFilePath, 0, 160, 90);

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

        System.Diagnostics.Debug.WriteLine($"Thumbnail saved: {thumbnailPath}");

        return thumbnailPath;
    }
}

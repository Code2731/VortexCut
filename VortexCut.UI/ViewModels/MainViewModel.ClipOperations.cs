using System;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Models;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// MainViewModel — 클립/재생/Export 관련 커맨드
/// </summary>
public partial class MainViewModel
{
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
            SourceMonitor.Reset();
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

    /// <summary>
    /// Project Bin 더블클릭 → Clip Monitor에 클립 로드
    /// </summary>
    public void LoadClipToSourceMonitor(MediaItem item)
    {
        SourceMonitor.LoadClip(item);
    }

    /// <summary>
    /// Clip Monitor에서 In/Out 범위를 타임라인에 추가
    /// 겹침 감지: 재생헤드 위치에서 빈 트랙 자동 선택, 없으면 끝에 append
    /// </summary>
    [RelayCommand]
    private void AddToTimelineFromSource()
    {
        var item = SourceMonitor.LoadedItem;
        if (item == null)
        {
            _toastService?.ShowError("오류", "Clip Monitor에 로드된 클립이 없습니다.");
            return;
        }

        try
        {
            var (trimStartMs, durationMs) = SourceMonitor.GetInsertRange();
            if (durationMs <= 0)
            {
                _toastService?.ShowError("오류", "유효한 범위가 아닙니다.");
                return;
            }

            // 겹치지 않는 트랙과 위치 자동 탐색
            var (trackIndex, startTimeMs) = Timeline.FindInsertPosition(durationMs);

            var clip = _projectService.AddVideoClip(
                item.FilePath, startTimeMs, durationMs, trackIndex, item.ProxyFilePath);

            // In 포인트가 있으면 Trim 설정
            if (trimStartMs > 0)
            {
                clip.TrimStartMs = trimStartMs;
                _projectService.SetClipTrim(clip.Id, trimStartMs, trimStartMs + durationMs);
            }

            Timeline.Clips.Add(clip);
            Preview.RenderFrameAsync(startTimeMs);

            var trackName = trackIndex < Timeline.VideoTracks.Count
                ? Timeline.VideoTracks[trackIndex].Name : $"V{trackIndex + 1}";
            _toastService?.ShowSuccess("타임라인 추가", $"{item.Name} → {trackName} @ {startTimeMs}ms");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddToTimelineFromSource ERROR: {ex.Message}");
            _toastService?.ShowError("타임라인 추가 실패", ex.Message);
        }
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        try
        {
            await Preview.TogglePlayback();
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
    /// Whisper 자동 자막 다이얼로그 열기 요청 (MainWindow에서 핸들링)
    /// </summary>
    public Action? RequestOpenWhisperDialog { get; set; }

    [RelayCommand]
    private void AutoSubtitle()
    {
        // 타임라인에 미디어 클립이 없으면 경고
        if (Timeline.Clips.Count == 0)
        {
            _toastService?.ShowError("자동 자막 불가", "타임라인에 클립이 없습니다.");
            return;
        }

        RequestOpenWhisperDialog?.Invoke();
    }
}

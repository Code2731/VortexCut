using System.Collections.ObjectModel;
using VortexCut.Core.Interfaces;
using VortexCut.Core.Models;

namespace VortexCut.UI.Services.Actions;

/// <summary>
/// 클립 추가 액션 (FFI 연동)
/// </summary>
public class AddClipAction : IUndoableAction
{
    private readonly ObservableCollection<ClipModel> _clips;
    private readonly IProjectService _projectService;
    private readonly string _filePath;
    private readonly long _startTimeMs;
    private readonly long _durationMs;
    private readonly int _trackIndex;
    private readonly string? _proxyFilePath;
    private ClipModel? _clip;

    public string Description => "클립 추가";

    public AddClipAction(
        ObservableCollection<ClipModel> clips,
        IProjectService projectService,
        string filePath,
        long startTimeMs,
        long durationMs,
        int trackIndex,
        string? proxyFilePath = null)
    {
        _clips = clips;
        _projectService = projectService;
        _filePath = filePath;
        _startTimeMs = startTimeMs;
        _durationMs = durationMs;
        _trackIndex = trackIndex;
        _proxyFilePath = proxyFilePath;
    }

    public void Execute()
    {
        _clip = _projectService.AddVideoClip(_filePath, _startTimeMs, _durationMs, _trackIndex, _proxyFilePath);
        _clips.Add(_clip);
    }

    public void Undo()
    {
        if (_clip == null) return;
        _clips.Remove(_clip);
        _projectService.RemoveVideoClip(_clip.Id);
        _clip = null;
    }
}

/// <summary>
/// 클립 삭제 액션 (FFI 연동)
/// 삭제 전 전체 스냅샷 캡처 (키프레임 포함)
/// </summary>
public class DeleteClipAction : IUndoableAction
{
    private readonly ObservableCollection<ClipModel> _clips;
    private readonly IProjectService _projectService;
    private readonly ClipModel _clip;

    // 스냅샷 데이터 (복원용)
    private readonly ulong _originalId;
    private readonly string _filePath;
    private readonly long _startTimeMs;
    private readonly long _durationMs;
    private readonly int _trackIndex;
    private readonly string? _proxyFilePath;
    private readonly uint _colorLabelArgb;
    private readonly ulong? _linkedAudioClipId;
    private readonly ulong? _linkedVideoClipId;

    public string Description => "클립 삭제";

    public DeleteClipAction(
        ObservableCollection<ClipModel> clips,
        IProjectService projectService,
        ClipModel clip)
    {
        _clips = clips;
        _projectService = projectService;
        _clip = clip;

        // 스냅샷 캡처
        _originalId = clip.Id;
        _filePath = clip.FilePath;
        _startTimeMs = clip.StartTimeMs;
        _durationMs = clip.DurationMs;
        _trackIndex = clip.TrackIndex;
        _proxyFilePath = clip.ProxyFilePath;
        _colorLabelArgb = clip.ColorLabelArgb;
        _linkedAudioClipId = clip.LinkedAudioClipId;
        _linkedVideoClipId = clip.LinkedVideoClipId;
    }

    public void Execute()
    {
        _clips.Remove(_clip);
        _projectService.RemoveVideoClip(_clip.Id);
    }

    public void Undo()
    {
        // Rust FFI 재추가 → 새 clipId 발급
        var newClipId = _projectService.ReAddVideoClip(_filePath, _startTimeMs, _durationMs);

        // 스냅샷에서 복원
        _clip.Id = newClipId;
        _clip.FilePath = _filePath;
        _clip.StartTimeMs = _startTimeMs;
        _clip.DurationMs = _durationMs;
        _clip.TrackIndex = _trackIndex;
        _clip.ProxyFilePath = _proxyFilePath;
        _clip.ColorLabelArgb = _colorLabelArgb;
        _clip.LinkedAudioClipId = _linkedAudioClipId;
        _clip.LinkedVideoClipId = _linkedVideoClipId;

        _clips.Add(_clip);
    }
}

/// <summary>
/// 클립 이동 액션 (Rust 동기화 포함)
/// 드래그 완료 후 RecordAction으로 기록 → Undo/Redo 시 Rust도 갱신
/// </summary>
public class MoveClipAction : IUndoableAction
{
    private readonly ClipModel _clip;
    private readonly IProjectService? _projectService;
    private readonly long _oldStartTimeMs;
    private readonly int _oldTrackIndex;
    private readonly long _newStartTimeMs;
    private readonly int _newTrackIndex;

    public string Description => "클립 이동";

    public MoveClipAction(
        ClipModel clip,
        long oldStartTimeMs, int oldTrackIndex,
        long newStartTimeMs, int newTrackIndex,
        IProjectService? projectService = null)
    {
        _clip = clip;
        _oldStartTimeMs = oldStartTimeMs;
        _oldTrackIndex = oldTrackIndex;
        _newStartTimeMs = newStartTimeMs;
        _newTrackIndex = newTrackIndex;
        _projectService = projectService;
    }

    public void Execute()
    {
        _clip.StartTimeMs = _newStartTimeMs;
        _clip.TrackIndex = _newTrackIndex;
        _projectService?.SyncClipToRust(_clip);
    }

    public void Undo()
    {
        _clip.StartTimeMs = _oldStartTimeMs;
        _clip.TrackIndex = _oldTrackIndex;
        _projectService?.SyncClipToRust(_clip);
    }
}

/// <summary>
/// 클립 트림 액션 (Rust 동기화 포함)
/// 트림 완료 후 RecordAction으로 기록 → Undo/Redo 시 Rust도 갱신
/// </summary>
public class TrimClipAction : IUndoableAction
{
    private readonly ClipModel _clip;
    private readonly IProjectService? _projectService;
    private readonly long _oldStartTimeMs;
    private readonly long _oldDurationMs;
    private readonly long _newStartTimeMs;
    private readonly long _newDurationMs;

    public string Description => "클립 트림";

    public TrimClipAction(
        ClipModel clip,
        long oldStartTimeMs, long oldDurationMs,
        long newStartTimeMs, long newDurationMs,
        IProjectService? projectService = null)
    {
        _clip = clip;
        _oldStartTimeMs = oldStartTimeMs;
        _oldDurationMs = oldDurationMs;
        _newStartTimeMs = newStartTimeMs;
        _newDurationMs = newDurationMs;
        _projectService = projectService;
    }

    public void Execute()
    {
        _clip.StartTimeMs = _newStartTimeMs;
        _clip.DurationMs = _newDurationMs;
        _projectService?.SyncClipToRust(_clip);
    }

    public void Undo()
    {
        _clip.StartTimeMs = _oldStartTimeMs;
        _clip.DurationMs = _oldDurationMs;
        _projectService?.SyncClipToRust(_clip);
    }
}

using System.Collections.ObjectModel;
using VortexCut.Core.Interfaces;
using VortexCut.Core.Models;

namespace VortexCut.UI.Services.Actions;

/// <summary>
/// 마커 추가 액션
/// </summary>
public class AddMarkerAction : IUndoableAction
{
    private readonly ObservableCollection<MarkerModel> _markers;
    private readonly MarkerModel _marker;

    public string Description => "마커 추가";

    public AddMarkerAction(ObservableCollection<MarkerModel> markers, MarkerModel marker)
    {
        _markers = markers;
        _marker = marker;
    }

    public void Execute()
    {
        _markers.Add(_marker);
    }

    public void Undo()
    {
        _markers.Remove(_marker);
    }
}

/// <summary>
/// 마커 제거 액션
/// </summary>
public class RemoveMarkerAction : IUndoableAction
{
    private readonly ObservableCollection<MarkerModel> _markers;
    private readonly MarkerModel _marker;

    public string Description => "마커 삭제";

    public RemoveMarkerAction(ObservableCollection<MarkerModel> markers, MarkerModel marker)
    {
        _markers = markers;
        _marker = marker;
    }

    public void Execute()
    {
        _markers.Remove(_marker);
    }

    public void Undo()
    {
        _markers.Add(_marker);
    }
}

/// <summary>
/// 키프레임 추가 액션
/// </summary>
public class AddKeyframeAction : IUndoableAction
{
    private readonly KeyframeSystem _system;
    private readonly double _time;
    private readonly double _value;
    private readonly InterpolationType _interpolation;

    public string Description => "키프레임 추가";

    public AddKeyframeAction(KeyframeSystem system, double time, double value, InterpolationType interpolation)
    {
        _system = system;
        _time = time;
        _value = value;
        _interpolation = interpolation;
    }

    public void Execute()
    {
        _system.AddKeyframe(_time, _value, _interpolation);
    }

    public void Undo()
    {
        var kf = _system.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - _time) < 0.001);
        if (kf != null)
            _system.Keyframes.Remove(kf);
    }
}

/// <summary>
/// 키프레임 제거 액션
/// </summary>
public class RemoveKeyframeAction : IUndoableAction
{
    private readonly KeyframeSystem _system;
    private readonly Keyframe _snapshot;

    public string Description => "키프레임 삭제";

    public RemoveKeyframeAction(KeyframeSystem system, Keyframe keyframe)
    {
        _system = system;
        // 스냅샷 캡처
        _snapshot = new Keyframe(keyframe.Time, keyframe.Value, keyframe.Interpolation)
        {
            InHandle = keyframe.InHandle,
            OutHandle = keyframe.OutHandle
        };
    }

    public void Execute()
    {
        var kf = _system.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - _snapshot.Time) < 0.001);
        if (kf != null)
            _system.Keyframes.Remove(kf);
    }

    public void Undo()
    {
        _system.AddKeyframe(_snapshot.Time, _snapshot.Value, _snapshot.Interpolation);
        // 핸들 복원
        var restored = _system.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - _snapshot.Time) < 0.001);
        if (restored != null)
        {
            restored.InHandle = _snapshot.InHandle;
            restored.OutHandle = _snapshot.OutHandle;
        }
    }
}

/// <summary>
/// 키프레임 이동 액션
/// </summary>
public class MoveKeyframeAction : IUndoableAction
{
    private readonly KeyframeSystem _system;
    private readonly double _oldTime;
    private readonly double _oldValue;
    private readonly double _newTime;
    private readonly double _newValue;

    public string Description => "키프레임 이동";

    public MoveKeyframeAction(KeyframeSystem system, double oldTime, double oldValue, double newTime, double newValue)
    {
        _system = system;
        _oldTime = oldTime;
        _oldValue = oldValue;
        _newTime = newTime;
        _newValue = newValue;
    }

    public void Execute()
    {
        var kf = _system.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - _oldTime) < 0.001);
        if (kf != null)
        {
            kf.Time = _newTime;
            kf.Value = _newValue;
            _system.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
    }

    public void Undo()
    {
        var kf = _system.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - _newTime) < 0.001);
        if (kf != null)
        {
            kf.Time = _oldTime;
            kf.Value = _oldValue;
            _system.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
    }
}

/// <summary>
/// Razor 분할 액션 (Rust Timeline 동기화 포함)
/// 원본 클립 축소 + 새 클립 생성, 양쪽 Rust에 반영
/// </summary>
public class RazorSplitAction : IUndoableAction
{
    private readonly ObservableCollection<ClipModel> _clips;
    private readonly IProjectService _projectService;
    private readonly ClipModel _originalClip;
    private readonly long _cutTimeMs;

    // 원본 스냅샷
    private readonly long _originalDurationMs;

    // 새 클립 (분할 후)
    private ClipModel? _newClip;

    public string Description => "클립 분할";

    public RazorSplitAction(
        ObservableCollection<ClipModel> clips,
        IProjectService projectService,
        ClipModel originalClip,
        long cutTimeMs)
    {
        _clips = clips;
        _projectService = projectService;
        _originalClip = originalClip;
        _cutTimeMs = cutTimeMs;
        _originalDurationMs = originalClip.DurationMs;
    }

    public void Execute()
    {
        long leftDuration = _cutTimeMs - _originalClip.StartTimeMs;
        long rightDuration = _originalDurationMs - leftDuration;

        // 좌측 클립: 원본 축소 + Rust 동기화 (trim_start 유지)
        _originalClip.DurationMs = leftDuration;
        _projectService.SyncClipToRust(_originalClip);

        // 우측 클립: 소스 오프셋 계산
        // 원본 TrimStartMs + 좌측 길이 = 우측의 소스 시작점
        long rightTrimStart = _originalClip.TrimStartMs + leftDuration;

        // 새 클립 생성 (우측 부분) + Rust에 추가
        var rightClipId = _projectService.ReAddVideoClip(
            _originalClip.FilePath, _cutTimeMs, rightDuration);

        _newClip = new ClipModel
        {
            Id = rightClipId,
            FilePath = _originalClip.FilePath,
            StartTimeMs = _cutTimeMs,
            DurationMs = rightDuration,
            TrackIndex = _originalClip.TrackIndex,
            TrimStartMs = rightTrimStart,
            SourceDurationMs = _originalClip.SourceDurationMs,
            ProxyFilePath = _originalClip.ProxyFilePath,
            ColorLabelArgb = _originalClip.ColorLabelArgb
        };

        // Rust에 trim 설정 (소스 오프셋 반영)
        _projectService.SetClipTrim(rightClipId, rightTrimStart, rightTrimStart + rightDuration);

        _clips.Add(_newClip);
    }

    public void Undo()
    {
        // 새 클립 제거 (Rust + C#)
        if (_newClip != null)
        {
            _projectService.RemoveVideoClip(_newClip.Id);
            _clips.Remove(_newClip);
        }

        // 원본 복원 + Rust 동기화 (원본 TrimStartMs 유지)
        _originalClip.DurationMs = _originalDurationMs;
        _projectService.SyncClipToRust(_originalClip);

        _newClip = null;
    }
}

/// <summary>
/// Ripple 삭제 액션 (Rust 동기화 포함)
/// 클립 삭제 + 후속 클립 좌측 이동, Rust Timeline도 갱신
/// </summary>
public class RippleDeleteAction : IUndoableAction
{
    private readonly ObservableCollection<ClipModel> _clips;
    private readonly IProjectService? _projectService;
    private readonly ClipModel _deletedClip;
    private readonly long _deletedDuration;
    private readonly int _trackIndex;
    private readonly List<(ClipModel clip, long originalStart)> _shiftedClips = new();

    public string Description => "리플 삭제";

    public RippleDeleteAction(
        ObservableCollection<ClipModel> clips,
        ClipModel clip,
        IProjectService? projectService = null)
    {
        _clips = clips;
        _projectService = projectService;
        _deletedClip = clip;
        _deletedDuration = clip.DurationMs;
        _trackIndex = clip.TrackIndex;

        // 후속 클립 스냅샷 (같은 트랙, 삭제 클립 이후)
        foreach (var c in clips.Where(c => c.TrackIndex == _trackIndex && c.StartTimeMs >= clip.EndTimeMs))
        {
            _shiftedClips.Add((c, c.StartTimeMs));
        }
    }

    public void Execute()
    {
        _clips.Remove(_deletedClip);
        _projectService?.RemoveVideoClip(_deletedClip.Id);

        // 후속 클립 좌측 이동 + Rust 동기화
        foreach (var (clip, _) in _shiftedClips)
        {
            clip.StartTimeMs -= _deletedDuration;
            _projectService?.SyncClipToRust(clip);
        }
    }

    public void Undo()
    {
        // 후속 클립 원위치 복원 + Rust 동기화
        foreach (var (clip, originalStart) in _shiftedClips)
        {
            clip.StartTimeMs = originalStart;
            _projectService?.SyncClipToRust(clip);
        }

        // 삭제된 클립 복원 + Rust 재등록
        if (_projectService != null)
        {
            var newId = _projectService.ReAddVideoClip(
                _deletedClip.FilePath, _deletedClip.StartTimeMs, _deletedClip.DurationMs);
            _deletedClip.Id = newId;
        }
        _clips.Add(_deletedClip);
    }
}

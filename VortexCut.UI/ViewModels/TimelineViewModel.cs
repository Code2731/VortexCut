using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Interfaces;
using VortexCut.Core.Models;
using VortexCut.Core.Services;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// 키프레임 시스템 타입
/// </summary>
public enum KeyframeSystemType
{
    Opacity,
    Volume,
    PositionX,
    PositionY,
    Scale,
    Rotation
}

/// <summary>
/// 타임라인 ViewModel
/// </summary>
public partial class TimelineViewModel : ViewModelBase
{
    private readonly IProjectService _projectService;
    private readonly UndoRedoService _undoRedoService;
    private ulong _nextTrackId = 1;

    [ObservableProperty]
    private ObservableCollection<ClipModel> _clips = new();

    [ObservableProperty]
    private ObservableCollection<TrackModel> _videoTracks = new();

    [ObservableProperty]
    private ObservableCollection<TrackModel> _audioTracks = new();

    [ObservableProperty]
    private ObservableCollection<TrackModel> _subtitleTracks = new();

    // 자막 클립 ID 발급용 카운터
    private ulong _nextSubtitleClipId = 100000;

    [ObservableProperty]
    private long _currentTimeMs = 0;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private ClipModel? _selectedClip;

    [ObservableProperty]
    private ObservableCollection<ClipModel> _selectedClips = new();

    [ObservableProperty]
    private ObservableCollection<MarkerModel> _markers = new();

    // Snap 설정
    [ObservableProperty]
    private bool _snapEnabled = true;

    [ObservableProperty]
    private long _snapThresholdMs = 100;

    // Razor 모드
    [ObservableProperty]
    private bool _razorModeEnabled = false;

    // 키프레임 시스템 선택
    [ObservableProperty]
    private KeyframeSystemType _selectedKeyframeSystem = KeyframeSystemType.Opacity;

    // Ripple 편집 모드
    [ObservableProperty]
    private bool _rippleModeEnabled = false;

    // In/Out 포인트 (워크에어리어)
    [ObservableProperty]
    private long? _inPointMs = null;

    [ObservableProperty]
    private long? _outPointMs = null;

    // 재생 중 여부
    [ObservableProperty]
    private bool _isPlaying = false;

    // 프로젝트 FPS (SMPTE 타임코드용)
    [ObservableProperty]
    private int _projectFps = 30;

    // 전역 클립 표시 모드 (개별 트랙 설정이 우선)
    [ObservableProperty]
    private ClipDisplayMode _globalDisplayMode = ClipDisplayMode.Filmstrip;

    // 오디오 파형 표시 모드
    [ObservableProperty]
    private WaveformDisplayMode _waveformMode = WaveformDisplayMode.NonRectified;

    // 현재 타임라인에서 화면에 보이는 시간 범위 (Visible Range)
    [ObservableProperty]
    private long _visibleStartMs = 0;

    [ObservableProperty]
    private long _visibleEndMs = 0;

    public RazorTool? RazorTool { get; private set; }
    public RippleEditService? RippleEditService { get; private set; }
    public LinkClipService? LinkClipService { get; private set; }
    public UndoRedoService UndoRedo => _undoRedoService;
    public IProjectService ProjectServiceRef => _projectService;

    /// <summary>
    /// 재생 중지 요청 콜백 (MainViewModel에서 설정)
    /// </summary>
    public Action? RequestStopPlayback { get; set; }

    public TimelineViewModel(IProjectService projectService)
    {
        _projectService = projectService;
        _undoRedoService = new UndoRedoService();

        // Undo/Redo 후 렌더 캐시 클리어
        _undoRedoService.OnHistoryChanged = () =>
        {
            _projectService.ClearRenderCache();
        };

        InitializeDefaultTracks();
        RazorTool = new RazorTool(this);
        RippleEditService = new RippleEditService(this);
        LinkClipService = new LinkClipService(this);
    }

    /// <summary>
    /// 기본 트랙 초기화 (6개 비디오 + 4개 오디오 + 1개 자막)
    /// </summary>
    private void InitializeDefaultTracks()
    {
        // 6개 비디오 트랙
        for (int i = 0; i < 6; i++)
        {
            AddVideoTrack();
        }

        // 4개 오디오 트랙
        for (int i = 0; i < 4; i++)
        {
            AddAudioTrack();
        }

        // 1개 자막 트랙
        AddSubtitleTrack();
    }

    /// <summary>
    /// 비디오 파일 추가
    /// </summary>
    public async Task AddVideoClipAsync(string filePath, string? proxyFilePath = null)
    {
        System.Diagnostics.Debug.WriteLine($"🎬 AddVideoClipAsync START: {filePath}");
        System.Diagnostics.Debug.WriteLine($"   CurrentTimeMs: {CurrentTimeMs}, Clips.Count: {Clips.Count}");

        await Task.Run(() =>
        {
            // Rust FFI로 실제 비디오 정보 조회
            var videoInfo = _projectService.GetVideoInfo(filePath);
            long durationMs = videoInfo.DurationMs;
            System.Diagnostics.Debug.WriteLine($"   📋 VideoInfo: duration={durationMs}ms, {videoInfo.Width}x{videoInfo.Height}, fps={videoInfo.Fps:F2}");

            // duration이 0이면 fallback (메타데이터 없는 파일)
            if (durationMs <= 0)
            {
                durationMs = 5000;
                System.Diagnostics.Debug.WriteLine($"   ⚠️ Duration is 0, using fallback: {durationMs}ms");
            }

            System.Diagnostics.Debug.WriteLine($"   Calling _projectService.AddVideoClip...");
            var clip = _projectService.AddVideoClip(filePath, CurrentTimeMs, durationMs, 0, proxyFilePath);
            System.Diagnostics.Debug.WriteLine($"   ✅ Clip created: ID={clip.Id}, StartTimeMs={clip.StartTimeMs}, DurationMs={clip.DurationMs}, TrackIndex={clip.TrackIndex}");

            // UI 스레드에서 실행
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                System.Diagnostics.Debug.WriteLine($"   🔵 Dispatcher.UIThread.Post - Adding clip to Clips collection...");
                Clips.Add(clip);
                System.Diagnostics.Debug.WriteLine($"   ✅ Clip added! Clips.Count is now: {Clips.Count}");
            });
        });

        System.Diagnostics.Debug.WriteLine($"🎬 AddVideoClipAsync END (but Post might not have executed yet)");
    }

    /// <summary>
    /// MediaItem으로부터 클립 추가 (드래그앤드롭용)
    /// </summary>
    public void AddClipFromMediaItem(MediaItem mediaItem, long startTimeMs, int trackIndex)
    {
        var clip = _projectService.AddVideoClip(
            mediaItem.FilePath,
            startTimeMs,
            mediaItem.DurationMs,
            trackIndex,
            mediaItem.ProxyFilePath);
        Clips.Add(clip);
    }

    /// <summary>
    /// 새 클립을 삽입할 최적의 트랙과 시작 위치를 찾음
    /// 1) 현재 재생헤드 위치에서 빈 비디오 트랙 검색
    /// 2) 모든 트랙이 겹치면 → 트랙 0의 기존 클립 끝에 append
    /// </summary>
    /// <returns>(trackIndex, startTimeMs)</returns>
    public (int trackIndex, long startTimeMs) FindInsertPosition(long durationMs)
    {
        long playheadMs = CurrentTimeMs;

        // 1) 재생헤드 위치에서 겹치지 않는 비디오 트랙 찾기
        for (int i = 0; i < VideoTracks.Count; i++)
        {
            bool hasOverlap = false;
            foreach (var clip in Clips)
            {
                if (clip.TrackIndex != i) continue;
                // 비디오 트랙만 (자막/오디오 트랙 인덱스는 VideoTracks.Count 이상)
                long clipEnd = clip.StartTimeMs + clip.DurationMs;
                if (playheadMs < clipEnd && (playheadMs + durationMs) > clip.StartTimeMs)
                {
                    hasOverlap = true;
                    break;
                }
            }
            if (!hasOverlap)
                return (i, playheadMs);
        }

        // 2) 모든 트랙이 겹침 → 트랙 0의 마지막 클립 끝에 append
        long maxEndMs = 0;
        foreach (var clip in Clips)
        {
            if (clip.TrackIndex < VideoTracks.Count)
            {
                long clipEnd = clip.StartTimeMs + clip.DurationMs;
                if (clipEnd > maxEndMs) maxEndMs = clipEnd;
            }
        }

        return (0, maxEndMs);
    }

    /// <summary>
    /// 타임라인 초기화
    /// </summary>
    public void Reset()
    {
        Clips.Clear();
        CurrentTimeMs = 0;
        SelectedClip = null;
        _undoRedoService.Clear();
    }

    /// <summary>
    /// 드래그/트림 중 여부 (Undo 차단용, ClipCanvasPanel에서 설정)
    /// </summary>
    public bool IsEditing { get; set; }

    /// <summary>
    /// Undo (Ctrl+Z) — 드래그/트림 중에는 차단
    /// </summary>
    [RelayCommand]
    public void Undo()
    {
        if (IsEditing) return;
        _undoRedoService.Undo();
    }

    /// <summary>
    /// Redo (Ctrl+Shift+Z / Ctrl+Y) — 드래그/트림 중에는 차단
    /// </summary>
    [RelayCommand]
    public void Redo()
    {
        if (IsEditing) return;
        _undoRedoService.Redo();
    }

    [RelayCommand]
    private void SelectClip(ClipModel clip)
    {
        SelectedClip = clip;
    }

    [RelayCommand]
    private void DeleteSelectedClip()
    {
        if (SelectedClip != null)
        {
            if (RippleModeEnabled)
            {
                // 리플 모드: RippleDeleteAction으로 Undo 지원
                var action = new Services.Actions.RippleDeleteAction(Clips, SelectedClip, _projectService);
                _undoRedoService.ExecuteAction(action);
            }
            else
            {
                // 일반 모드: DeleteClipAction (FFI 연동)
                var action = new Services.Actions.DeleteClipAction(Clips, _projectService, SelectedClip);
                _undoRedoService.ExecuteAction(action);
            }
            SelectedClip = null;
        }
    }

    /// <summary>
    /// 비디오 트랙 추가
    /// </summary>
    [RelayCommand]
    public void AddVideoTrack()
    {
        // TODO: ProjectService.AddVideoTrack() 연동
        var track = new TrackModel
        {
            Id = _nextTrackId++,
            Index = VideoTracks.Count,
            Type = TrackType.Video,
            Name = $"V{VideoTracks.Count + 1}",
            ColorArgb = 0xFF5DA8E8 // 밝은 블루
        };
        VideoTracks.Add(track);
    }

    /// <summary>
    /// 오디오 트랙 추가
    /// </summary>
    [RelayCommand]
    public void AddAudioTrack()
    {
        // TODO: ProjectService.AddAudioTrack() 연동
        var track = new TrackModel
        {
            Id = _nextTrackId++,
            Index = AudioTracks.Count,
            Type = TrackType.Audio,
            Name = $"A{AudioTracks.Count + 1}",
            ColorArgb = 0xFF6CCB6C // 밝은 그린
        };
        AudioTracks.Add(track);
    }

    /// <summary>
    /// 자막 트랙 추가
    /// </summary>
    [RelayCommand]
    public void AddSubtitleTrack()
    {
        var track = new TrackModel
        {
            Id = _nextTrackId++,
            Index = SubtitleTracks.Count,
            Type = TrackType.Subtitle,
            Name = $"S{SubtitleTracks.Count + 1}",
            ColorArgb = 0xFFFFC857, // 앰버
            Height = 40 // 자막 트랙은 약간 작게
        };
        SubtitleTracks.Add(track);
    }

    /// <summary>
    /// 트랙 제거
    /// </summary>
    public void RemoveTrack(TrackModel track)
    {
        var list = track.Type switch
        {
            TrackType.Video => VideoTracks,
            TrackType.Audio => AudioTracks,
            TrackType.Subtitle => SubtitleTracks,
            _ => VideoTracks
        };
        list.Remove(track);
        for (int i = 0; i < list.Count; i++)
            list[i].Index = i;
    }

    /// <summary>
    /// SRT 파일 임포트 → 자막 클립 생성
    /// </summary>
    public void ImportSrt(string filePath, int trackIndex = 0)
    {
        var entries = SrtParser.Parse(filePath);
        if (entries.Count == 0) return;

        // 자막 트랙이 없으면 추가
        if (SubtitleTracks.Count == 0)
            AddSubtitleTrack();

        var actions = new List<Core.Interfaces.IUndoableAction>();
        foreach (var entry in entries)
        {
            var clip = new SubtitleClipModel(
                _nextSubtitleClipId++,
                entry.StartMs,
                entry.EndMs - entry.StartMs,
                entry.Text,
                trackIndex);

            actions.Add(new Services.Actions.AddSubtitleClipAction(Clips, clip));
        }

        if (actions.Count == 1)
            _undoRedoService.ExecuteAction(actions[0]);
        else
            _undoRedoService.ExecuteAction(new Services.Actions.CompositeAction("SRT 임포트", actions));
    }

    /// <summary>
    /// 자막 클립 → SRT 파일 내보내기
    /// </summary>
    public void ExportSrt(string filePath, int trackIndex = 0)
    {
        var subtitleClips = Clips
            .OfType<SubtitleClipModel>()
            .Where(c => c.TrackIndex == trackIndex)
            .OrderBy(c => c.StartTimeMs)
            .ToList();

        var entries = subtitleClips.Select((c, i) =>
            new SubtitleEntry(i + 1, c.StartTimeMs, c.EndTimeMs, c.Text))
            .ToList();

        SrtParser.Export(filePath, entries);
    }

    /// <summary>
    /// 특정 시간에 표시할 자막 텍스트 가져오기
    /// </summary>
    public string? GetSubtitleTextAt(long timeMs)
    {
        return Clips
            .OfType<SubtitleClipModel>()
            .FirstOrDefault(c => timeMs >= c.StartTimeMs && timeMs < c.EndTimeMs)
            ?.Text;
    }

    /// <summary>
    /// 마커 추가 (Undo 지원)
    /// </summary>
    public void AddMarker(long timeMs, string name = "", MarkerType type = MarkerType.Comment)
    {
        var marker = new MarkerModel
        {
            Id = (ulong)(Markers.Count + 1),
            TimeMs = timeMs,
            Name = name,
            Type = type
        };
        var action = new Services.Actions.AddMarkerAction(Markers, marker);
        _undoRedoService.ExecuteAction(action);
    }

    /// <summary>
    /// 현재 Playhead 위치에 마커 추가
    /// </summary>
    [RelayCommand]
    public void AddMarkerAtCurrentTime()
    {
        AddMarker(CurrentTimeMs, $"Marker {Markers.Count + 1}");
    }

    /// <summary>
    /// 마커 제거 (Undo 지원)
    /// </summary>
    [RelayCommand]
    public void RemoveMarker(MarkerModel marker)
    {
        var action = new Services.Actions.RemoveMarkerAction(Markers, marker);
        _undoRedoService.ExecuteAction(action);
    }

    /// <summary>
    /// 클립에서 키프레임 시스템 가져오기
    /// </summary>
    private KeyframeSystem? GetKeyframeSystem(ClipModel clip, KeyframeSystemType type)
    {
        return type switch
        {
            KeyframeSystemType.Opacity => clip.OpacityKeyframes,
            KeyframeSystemType.Volume => clip.VolumeKeyframes,
            KeyframeSystemType.PositionX => clip.PositionXKeyframes,
            KeyframeSystemType.PositionY => clip.PositionYKeyframes,
            KeyframeSystemType.Scale => clip.ScaleKeyframes,
            KeyframeSystemType.Rotation => clip.RotationKeyframes,
            _ => null
        };
    }

    /// <summary>
    /// 현재 Playhead 위치에 키프레임 추가 (K 키, Undo 지원)
    /// </summary>
    [RelayCommand]
    public void AddKeyframeAtCurrentTime()
    {
        if (SelectedClips.Count == 0) return;

        var clip = SelectedClips.First();
        var keyframeSystem = GetKeyframeSystem(clip, SelectedKeyframeSystem);
        if (keyframeSystem == null) return;

        // 클립 시작 기준 상대 시간 (초)
        double relativeTime = (CurrentTimeMs - clip.StartTimeMs) / 1000.0;
        if (relativeTime < 0 || relativeTime > clip.DurationMs / 1000.0)
            return; // 클립 범위 밖

        // 현재 보간된 값 사용 (키프레임이 있으면 보간, 없으면 50.0 기본값)
        double currentValue = keyframeSystem.Keyframes.Count > 0
            ? keyframeSystem.Interpolate(relativeTime)
            : 50.0;

        var action = new Services.Actions.AddKeyframeAction(
            keyframeSystem, relativeTime, currentValue, InterpolationType.Linear);
        _undoRedoService.ExecuteAction(action);
    }

    /// <summary>
    /// In 포인트 설정 (I 키)
    /// </summary>
    [RelayCommand]
    public void SetInPoint(long timeMs)
    {
        InPointMs = timeMs;
    }

    /// <summary>
    /// Out 포인트 설정 (O 키)
    /// </summary>
    [RelayCommand]
    public void SetOutPoint(long timeMs)
    {
        OutPointMs = timeMs;
    }

    /// <summary>
    /// In/Out 포인트 지우기
    /// </summary>
    [RelayCommand]
    public void ClearInOutPoints()
    {
        InPointMs = null;
        OutPointMs = null;
    }

    /// <summary>
    /// 재생/일시정지 토글 (Space 키)
    /// </summary>
    [RelayCommand]
    public void TogglePlayback()
    {
        IsPlaying = !IsPlaying;
        // TODO: 실제 재생 로직 구현 (PreviewViewModel과 연동)
    }

    /// <summary>
    /// 전역 클립 표시 모드 순환 (Ctrl+Shift+T)
    /// 모든 트랙을 동일 모드로 일괄 변경
    /// </summary>
    [RelayCommand]
    public void CycleGlobalDisplayMode()
    {
        GlobalDisplayMode = GlobalDisplayMode switch
        {
            ClipDisplayMode.Filmstrip => ClipDisplayMode.Thumbnail,
            ClipDisplayMode.Thumbnail => ClipDisplayMode.Minimal,
            ClipDisplayMode.Minimal => ClipDisplayMode.Filmstrip,
            _ => ClipDisplayMode.Filmstrip
        };

        // 모든 트랙에 적용
        foreach (var track in VideoTracks)
            track.DisplayMode = GlobalDisplayMode;
        foreach (var track in AudioTracks)
            track.DisplayMode = GlobalDisplayMode;
        foreach (var track in SubtitleTracks)
            track.DisplayMode = GlobalDisplayMode;
    }
}

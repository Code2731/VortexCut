using VortexCut.Core.Models;
using VortexCut.Core.Serialization;
using VortexCut.UI.ViewModels;
using VortexCut.Interop.Services;
using System.Linq;

namespace VortexCut.UI.Services;

/// <summary>
/// 프로젝트 관리 서비스 (Rust Timeline/Renderer 연동)
/// </summary>
public class ProjectService : IDisposable
{
    private readonly TimelineService _timelineService;
    private readonly RenderService _renderService;
    private Project? _currentProject;
    private ulong _defaultVideoTrackId;
    private TimelineHandle? _timelineHandle;

    public Project? CurrentProject => _currentProject;

    /// <summary>
    /// Rust Timeline의 원시 포인터 (Export용)
    /// </summary>
    public IntPtr TimelineRawHandle => _timelineHandle?.DangerousGetHandle() ?? IntPtr.Zero;

    public ProjectService()
    {
        _timelineService = new TimelineService();
        _renderService = new RenderService();
    }

    /// <summary>
    /// 새 프로젝트 생성
    /// </summary>
    public void CreateProject(string name, uint width = 1920, uint height = 1080, double fps = 30.0)
    {
        System.Diagnostics.Debug.WriteLine($"🎬 ProjectService.CreateProject START: {name}, {width}x{height}, {fps}fps");

        try
        {
            // 중요: 리소스 해제 순서
            // 1. Renderer 먼저 해제 (타임라인을 참조하고 있음)
            // 2. Timeline 해제
            System.Diagnostics.Debug.WriteLine("   [1/6] Destroying old renderer...");
            _renderService.DestroyRenderer();

            System.Diagnostics.Debug.WriteLine("   [2/6] Destroying old timeline...");
            _timelineService.DestroyTimeline();

            // 새 프로젝트 생성
            System.Diagnostics.Debug.WriteLine("   [3/6] Creating new project...");
            _currentProject = new Project(name, width, height, fps);

            System.Diagnostics.Debug.WriteLine("   [4/6] Creating timeline...");
            _timelineService.CreateTimeline(width, height, fps);

            // 기본 비디오 트랙 생성
            System.Diagnostics.Debug.WriteLine("   [5/6] Adding video track...");
            _defaultVideoTrackId = _timelineService.AddVideoTrack();
            System.Diagnostics.Debug.WriteLine($"       Default track ID: {_defaultVideoTrackId}");

            // Renderer 생성 (TimelineHandle 가져오기)
            System.Diagnostics.Debug.WriteLine("   [6/6] Creating renderer...");
            _timelineHandle = _timelineService.GetTimelineHandle();
            _renderService.CreateRenderer(_timelineHandle);

            System.Diagnostics.Debug.WriteLine("   ✅ ProjectService.CreateProject COMPLETE");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"   ❌ ProjectService.CreateProject FAILED: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 비디오 클립 추가
    /// </summary>
    public ClipModel AddVideoClip(string filePath, long startTimeMs, long durationMs, int trackIndex = 0, string? proxyFilePath = null)
    {
        if (_currentProject == null)
            throw new InvalidOperationException("No project is open");

        System.Diagnostics.Debug.WriteLine($"📹 ProjectService.AddVideoClip: trackId={_defaultVideoTrackId}, filePath={filePath}");
        System.Diagnostics.Debug.WriteLine($"   startTimeMs={startTimeMs}, durationMs={durationMs}");

        var clipId = _timelineService.AddVideoClip(_defaultVideoTrackId, filePath, startTimeMs, durationMs);

        System.Diagnostics.Debug.WriteLine($"   ✅ Rust returned clipId={clipId}");

        // Timeline 상태 확인
        var videoTrackCount = _timelineService.GetVideoTrackCount();
        var audioTrackCount = _timelineService.GetAudioTrackCount();
        var clipCount = _timelineService.GetVideoClipCount(_defaultVideoTrackId);
        var duration = _timelineService.GetDuration();

        System.Diagnostics.Debug.WriteLine($"   📊 Timeline state: videoTracks={videoTrackCount}, audioTracks={audioTrackCount}, clipCount={clipCount}, duration={duration}ms");

        var clip = new ClipModel(clipId, filePath, startTimeMs, durationMs, trackIndex)
        {
            ProxyFilePath = proxyFilePath
        };
        _currentProject.Clips.Add(clip);

        return clip;
    }

    /// <summary>
    /// 비디오 클립 제거 (Undo용)
    /// Razor 분할로 생성된 클립은 Rust에 없을 수 있으므로 FFI 실패 시 무시
    /// </summary>
    public void RemoveVideoClip(ulong clipId, ulong trackId = 0)
    {
        if (_currentProject == null) return;

        var rustTrackId = trackId > 0 ? trackId : _defaultVideoTrackId;
        try { _timelineService.RemoveVideoClip(rustTrackId, clipId); }
        catch { /* Razor 분할 클립 등 Rust에 미등록 시 무시 */ }
        _currentProject.Clips.RemoveAll(c => c.Id == clipId);
    }

    /// <summary>
    /// 오디오 클립 제거 (Undo용)
    /// </summary>
    public void RemoveAudioClip(ulong clipId, ulong trackId)
    {
        if (_currentProject == null) return;

        try { _timelineService.RemoveAudioClip(trackId, clipId); }
        catch { /* Rust에 미등록 시 무시 */ }
        _currentProject.Clips.RemoveAll(c => c.Id == clipId);
    }

    /// <summary>
    /// 비디오 클립 재추가 (Redo/Undo용) — 새 Rust clipId 반환
    /// _currentProject.Clips에도 추가하여 정합성 유지
    /// </summary>
    public ulong ReAddVideoClip(string filePath, long startTimeMs, long durationMs)
    {
        if (_currentProject == null)
            throw new InvalidOperationException("No project is open");

        var newId = _timelineService.AddVideoClip(_defaultVideoTrackId, filePath, startTimeMs, durationMs);
        return newId;
    }

    /// <summary>
    /// 클립을 Rust Timeline에 동기화 (remove + re-add + trim 설정)
    /// 드래그/트림/Razor 후 C# 모델이 변경되었을 때 호출
    /// 새 Rust clipId로 clip.Id 갱신
    /// </summary>
    public void SyncClipToRust(ClipModel clip)
    {
        if (_currentProject == null) return;

        // _currentProject.Clips에서 기존 항목 제거 (ID로 찾기)
        _currentProject.Clips.RemoveAll(c => c.Id == clip.Id);

        // Rust에서 기존 클립 제거 (없으면 무시)
        try { _timelineService.RemoveVideoClip(_defaultVideoTrackId, clip.Id); }
        catch { }

        // Rust에 새 클립 추가
        var newId = _timelineService.AddVideoClip(
            _defaultVideoTrackId, clip.FilePath, clip.StartTimeMs, clip.DurationMs);
        clip.Id = newId;

        // trim_start_ms가 0이 아닌 경우 Rust에 설정
        if (clip.TrimStartMs > 0)
        {
            try
            {
                _timelineService.SetVideoClipTrim(
                    _defaultVideoTrackId, newId,
                    clip.TrimStartMs, clip.TrimStartMs + clip.DurationMs);
            }
            catch { }
        }

        // _currentProject.Clips에도 추가
        _currentProject.Clips.Add(clip);
    }

    /// <summary>
    /// 비디오 클립의 Rust trim 값 설정
    /// </summary>
    public void SetClipTrim(ulong clipId, long trimStartMs, long trimEndMs)
    {
        try
        {
            _timelineService.SetVideoClipTrim(_defaultVideoTrackId, clipId, trimStartMs, trimEndMs);
        }
        catch { }
    }

    /// <summary>
    /// 클립 볼륨 설정 (Inspector Audio 탭에서 호출)
    /// </summary>
    public void SetClipVolume(ulong clipId, float volume)
    {
        try { _timelineService.SetClipVolume(clipId, volume); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 클립 속도 설정 (Inspector Audio 탭에서 호출)
    /// </summary>
    public void SetClipSpeed(ulong clipId, double speed)
    {
        try { _timelineService.SetClipSpeed(clipId, speed); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 클립 페이드 설정 (Inspector Audio 탭에서 호출)
    /// </summary>
    public void SetClipFade(ulong clipId, long fadeInMs, long fadeOutMs)
    {
        try { _timelineService.SetClipFade(clipId, fadeInMs, fadeOutMs); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 클립 이펙트 설정 (Inspector Color 탭에서 호출)
    /// </summary>
    public void SetClipEffects(ulong clipId, float brightness, float contrast, float saturation, float temperature)
    {
        try { _renderService.SetClipEffects(clipId, brightness, contrast, saturation, temperature); }
        catch { /* Renderer 미생성 시 무시 */ }
    }

    /// <summary>
    /// 렌더 캐시 클리어 (Undo/Redo 후 호출)
    /// </summary>
    public void ClearRenderCache()
    {
        try { _renderService.ClearCache(); }
        catch { /* Renderer 미생성 시 무시 */ }
    }

    /// <summary>
    /// 특정 시간의 프레임 렌더링 (프레임 스킵 시 null 반환)
    /// </summary>
    public RenderedFrame? RenderFrame(long timestampMs)
    {
        var frame = _renderService.RenderFrame(timestampMs);
        return frame;
    }

    /// <summary>
    /// 재생 모드 전환 (재생 시작 시 true, 정지 시 false)
    /// </summary>
    public void SetPlaybackMode(bool playback)
    {
        try { _renderService.SetPlaybackMode(playback); }
        catch { /* Renderer 미생성 시 무시 */ }
    }

    /// <summary>
    /// 현재 UI 상태로부터 ProjectData DTO를 생성 (저장용).
    /// </summary>
    public ProjectData ExtractProjectData(MainViewModel mainVm)
    {
        if (_currentProject == null)
        {
            throw new InvalidOperationException("No project is open");
        }

        var timelineVm = mainVm.Timeline;
        var data = new ProjectData
        {
            ProjectName = mainVm.ProjectName,
            Width = _currentProject.Width,
            Height = _currentProject.Height,
            Fps = _currentProject.Fps,
            SnapEnabled = timelineVm.SnapEnabled,
            SnapThresholdMs = timelineVm.SnapThresholdMs,
            InPointMs = timelineVm.InPointMs,
            OutPointMs = timelineVm.OutPointMs
        };

        // MediaItems
        foreach (var item in mainVm.ProjectBin.MediaItems)
        {
            data.MediaItems.Add(MediaItemToDto(item));
        }

        // Tracks (비디오 → 오디오 순)
        foreach (var track in timelineVm.VideoTracks)
        {
            data.VideoTracks.Add(TrackToDto(track));
        }

        foreach (var track in timelineVm.AudioTracks)
        {
            data.AudioTracks.Add(TrackToDto(track));
        }

        // 자막 트랙
        foreach (var track in timelineVm.SubtitleTracks)
        {
            data.SubtitleTracks.Add(TrackToDto(track));
        }

        // Clips
        foreach (var clip in timelineVm.Clips)
        {
            data.Clips.Add(ClipToDto(clip));
        }

        // Markers
        foreach (var marker in timelineVm.Markers)
        {
            data.Markers.Add(MarkerToDto(marker));
        }

        return data;
    }

    /// <summary>
    /// 저장된 ProjectData를 기반으로 전체 프로젝트/타임라인 상태 복원.
    /// </summary>
    public void RestoreProjectData(ProjectData data, MainViewModel mainVm)
    {
        // 1) 기존 렌더러/타임라인 정리
        _renderService.DestroyRenderer();
        _timelineService.DestroyTimeline();

        // 2) Project / Timeline 재생성
        _currentProject = new Project(data.ProjectName, data.Width, data.Height, data.Fps);
        _timelineService.CreateTimeline(data.Width, data.Height, data.Fps);

        _timelineHandle = _timelineService.GetTimelineHandle();
        _renderService.CreateRenderer(_timelineHandle);

        // 3) UI 뷰모델 초기화
        var timelineVm = mainVm.Timeline;
        timelineVm.Reset();
        mainVm.Preview.Reset();
        mainVm.ProjectBin.Clear();
        timelineVm.SubtitleTracks.Clear();

        mainVm.ProjectName = data.ProjectName;

        // Snap / In/Out 복원
        timelineVm.SnapEnabled = data.SnapEnabled;
        timelineVm.SnapThresholdMs = data.SnapThresholdMs;
        timelineVm.InPointMs = data.InPointMs;
        timelineVm.OutPointMs = data.OutPointMs;

        // 4) MediaItems 복원 (ProjectBin)
        foreach (var mediaDto in data.MediaItems)
        {
            var mediaItem = DtoToMediaItem(mediaDto);
            mainVm.ProjectBin.AddMediaItem(mediaItem);
        }

        // 5) Tracks 복원 (Rust + ViewModel)
        timelineVm.VideoTracks.Clear();
        timelineVm.AudioTracks.Clear();

        var combinedIndexToTrackId = new Dictionary<int, ulong>();
        int combinedIndex = 0;

        // 비디오 트랙
        for (int i = 0; i < data.VideoTracks.Count; i++)
        {
            var dto = data.VideoTracks[i];
            var trackId = _timelineService.AddVideoTrack();
            if (i == 0)
            {
                _defaultVideoTrackId = trackId;
            }

            var model = DtoToTrack(dto, trackId, TrackType.Video);
            timelineVm.VideoTracks.Add(model);

            combinedIndexToTrackId[combinedIndex++] = trackId;
        }

        // 오디오 트랙
        for (int i = 0; i < data.AudioTracks.Count; i++)
        {
            var dto = data.AudioTracks[i];
            var trackId = _timelineService.AddAudioTrack();
            var model = DtoToTrack(dto, trackId, TrackType.Audio);
            timelineVm.AudioTracks.Add(model);

            combinedIndexToTrackId[combinedIndex++] = trackId;
        }

        // 자막 트랙 복원 (Rust에 등록하지 않음 — C#만)
        int subtitleTrackStartIndex = combinedIndex;
        for (int i = 0; i < data.SubtitleTracks.Count; i++)
        {
            var dto = data.SubtitleTracks[i];
            var model = DtoToTrack(dto, (ulong)(1000 + i), TrackType.Subtitle);
            timelineVm.SubtitleTracks.Add(model);
            combinedIndex++;
        }

        // 6) Clips 복원 (Rust Timeline + Project + ViewModel)
        _currentProject.Clips.Clear();
        timelineVm.Clips.Clear();

        int subtitleCombinedStart = data.VideoTracks.Count + data.AudioTracks.Count;

        foreach (var clipDto in data.Clips)
        {
            // 자막 클립인지 판별
            bool isSubtitleClip = clipDto.TrackIndex >= subtitleCombinedStart
                                  || clipDto.SubtitleText != null;

            if (isSubtitleClip)
            {
                // 자막 클립은 Rust에 등록하지 않음
                var subtitleClip = DtoToSubtitleClip(clipDto);
                timelineVm.Clips.Add(subtitleClip);
                continue;
            }

            if (!combinedIndexToTrackId.TryGetValue(clipDto.TrackIndex, out var trackId))
            {
                // 매핑 실패 시 기본 비디오 트랙에 배치
                trackId = _defaultVideoTrackId;
            }

            // 비디오/오디오 트랙 구분 (combined index 기준)
            bool isAudioClip = clipDto.TrackIndex >= data.VideoTracks.Count;

            ulong clipId;
            if (isAudioClip)
            {
                clipId = _timelineService.AddAudioClip(trackId, clipDto.FilePath, clipDto.StartTimeMs, clipDto.DurationMs);
            }
            else
            {
                clipId = _timelineService.AddVideoClip(trackId, clipDto.FilePath, clipDto.StartTimeMs, clipDto.DurationMs);
            }

            var clipModel = DtoToClip(clipDto, clipId);
            _currentProject.Clips.Add(clipModel);
            timelineVm.Clips.Add(clipModel);

            // 이펙트가 있으면 Rust Renderer에 전달
            if (Math.Abs(clipModel.Brightness) > 0.001 || Math.Abs(clipModel.Contrast) > 0.001
                || Math.Abs(clipModel.Saturation) > 0.001 || Math.Abs(clipModel.Temperature) > 0.001)
            {
                try
                {
                    _renderService.SetClipEffects(clipId,
                        (float)clipModel.Brightness, (float)clipModel.Contrast,
                        (float)clipModel.Saturation, (float)clipModel.Temperature);
                }
                catch { /* Renderer busy 시 무시 */ }
            }

            // 볼륨/속도/페이드가 기본값이 아니면 Rust Timeline에 전달
            if (Math.Abs(clipModel.Volume - 1.0) > 0.001)
                SetClipVolume(clipId, (float)clipModel.Volume);
            if (Math.Abs(clipModel.Speed - 1.0) > 0.001)
                SetClipSpeed(clipId, clipModel.Speed);
            if (clipModel.FadeInMs > 0 || clipModel.FadeOutMs > 0)
                SetClipFade(clipId, clipModel.FadeInMs, clipModel.FadeOutMs);
        }

        // 7) Markers 복원 (ViewModel만)
        timelineVm.Markers.Clear();
        foreach (var markerDto in data.Markers)
        {
            var marker = DtoToMarker(markerDto);
            timelineVm.Markers.Add(marker);
        }
    }

    private static MediaItemData MediaItemToDto(MediaItem item) => new()
    {
        Name = item.Name,
        FilePath = item.FilePath,
        Type = item.Type,
        DurationMs = item.DurationMs,
        Width = item.Width,
        Height = item.Height,
        Fps = item.Fps,
        ProxyFilePath = item.ProxyFilePath
    };

    private static MediaItem DtoToMediaItem(MediaItemData data) => new()
    {
        Name = data.Name,
        FilePath = data.FilePath,
        Type = data.Type,
        DurationMs = data.DurationMs,
        Width = data.Width,
        Height = data.Height,
        Fps = data.Fps,
        ProxyFilePath = data.ProxyFilePath
    };

    private static TrackData TrackToDto(TrackModel track) => new()
    {
        Id = track.Id,
        Index = track.Index,
        Name = track.Name,
        IsEnabled = track.IsEnabled,
        IsMuted = track.IsMuted,
        IsSolo = track.IsSolo,
        IsLocked = track.IsLocked,
        ColorArgb = track.ColorArgb,
        Height = track.Height
    };

    private static TrackModel DtoToTrack(TrackData data, ulong id, TrackType type) => new()
    {
        Id = id,
        Index = data.Index,
        Type = type,
        Name = data.Name,
        IsEnabled = data.IsEnabled,
        IsMuted = data.IsMuted,
        IsSolo = data.IsSolo,
        IsLocked = data.IsLocked,
        ColorArgb = data.ColorArgb,
        Height = data.Height
    };

    private static ClipData ClipToDto(ClipModel clip)
    {
        var dto = new ClipData
        {
            Id = clip.Id,
            FilePath = clip.FilePath,
            StartTimeMs = clip.StartTimeMs,
            DurationMs = clip.DurationMs,
            TrackIndex = clip.TrackIndex,
            ColorLabelArgb = clip.ColorLabelArgb,
            LinkedAudioClipId = clip.LinkedAudioClipId,
            LinkedVideoClipId = clip.LinkedVideoClipId,
            Brightness = clip.Brightness,
            Contrast = clip.Contrast,
            Saturation = clip.Saturation,
            Temperature = clip.Temperature,
            Volume = clip.Volume,
            Speed = clip.Speed,
            FadeInMs = clip.FadeInMs,
            FadeOutMs = clip.FadeOutMs,
            OpacityKeyframes = KeyframeSystemToDto(clip.OpacityKeyframes),
            VolumeKeyframes = KeyframeSystemToDto(clip.VolumeKeyframes),
            PositionXKeyframes = KeyframeSystemToDto(clip.PositionXKeyframes),
            PositionYKeyframes = KeyframeSystemToDto(clip.PositionYKeyframes),
            ScaleKeyframes = KeyframeSystemToDto(clip.ScaleKeyframes),
            RotationKeyframes = KeyframeSystemToDto(clip.RotationKeyframes)
        };

        // 자막 클립 전용 필드
        if (clip is SubtitleClipModel subtitleClip)
        {
            dto.SubtitleText = subtitleClip.Text;
            dto.SubtitleStyle = SubtitleStyleToDto(subtitleClip.Style);
        }

        return dto;
    }

    private static ClipModel DtoToClip(ClipData data, ulong id)
    {
        var clip = new ClipModel(id, data.FilePath, data.StartTimeMs, data.DurationMs, data.TrackIndex)
        {
            ColorLabelArgb = data.ColorLabelArgb,
            LinkedAudioClipId = data.LinkedAudioClipId,
            LinkedVideoClipId = data.LinkedVideoClipId,
            Brightness = data.Brightness,
            Contrast = data.Contrast,
            Saturation = data.Saturation,
            Temperature = data.Temperature,
            Volume = data.Volume,
            Speed = data.Speed,
            FadeInMs = data.FadeInMs,
            FadeOutMs = data.FadeOutMs
        };

        ApplyKeyframeSystemData(data.OpacityKeyframes, clip.OpacityKeyframes);
        ApplyKeyframeSystemData(data.VolumeKeyframes, clip.VolumeKeyframes);
        ApplyKeyframeSystemData(data.PositionXKeyframes, clip.PositionXKeyframes);
        ApplyKeyframeSystemData(data.PositionYKeyframes, clip.PositionYKeyframes);
        ApplyKeyframeSystemData(data.ScaleKeyframes, clip.ScaleKeyframes);
        ApplyKeyframeSystemData(data.RotationKeyframes, clip.RotationKeyframes);

        return clip;
    }

    private static MarkerData MarkerToDto(MarkerModel marker) => new()
    {
        Id = marker.Id,
        TimeMs = marker.TimeMs,
        Name = marker.Name,
        Comment = marker.Comment,
        ColorArgb = marker.ColorArgb,
        Type = marker.Type,
        DurationMs = marker.DurationMs
    };

    private static MarkerModel DtoToMarker(MarkerData data) => new()
    {
        Id = data.Id,
        TimeMs = data.TimeMs,
        Name = data.Name,
        Comment = data.Comment,
        ColorArgb = data.ColorArgb,
        Type = data.Type,
        DurationMs = data.DurationMs
    };

    private static SubtitleClipModel DtoToSubtitleClip(ClipData data)
    {
        var clip = new SubtitleClipModel(
            data.Id, data.StartTimeMs, data.DurationMs,
            data.SubtitleText ?? string.Empty, data.TrackIndex);

        if (data.SubtitleStyle != null)
        {
            clip.Style = DtoToSubtitleStyle(data.SubtitleStyle);
        }

        return clip;
    }

    private static SubtitleStyleData SubtitleStyleToDto(SubtitleStyle style) => new()
    {
        FontFamily = style.FontFamily,
        FontSize = style.FontSize,
        FontColorArgb = style.FontColorArgb,
        OutlineColorArgb = style.OutlineColorArgb,
        OutlineThickness = style.OutlineThickness,
        BackgroundColorArgb = style.BackgroundColorArgb,
        Position = style.Position,
        IsBold = style.IsBold,
        IsItalic = style.IsItalic
    };

    private static SubtitleStyle DtoToSubtitleStyle(SubtitleStyleData data) => new()
    {
        FontFamily = data.FontFamily,
        FontSize = data.FontSize,
        FontColorArgb = data.FontColorArgb,
        OutlineColorArgb = data.OutlineColorArgb,
        OutlineThickness = data.OutlineThickness,
        BackgroundColorArgb = data.BackgroundColorArgb,
        Position = data.Position,
        IsBold = data.IsBold,
        IsItalic = data.IsItalic
    };

    private static KeyframeSystemData KeyframeSystemToDto(KeyframeSystem system)
    {
        var dto = new KeyframeSystemData();

        foreach (var kf in system.Keyframes)
        {
            dto.Keyframes.Add(new KeyframeData
            {
                Time = kf.Time,
                Value = kf.Value,
                Interpolation = kf.Interpolation,
                InHandle = kf.InHandle != null
                    ? new BezierHandleData
                    {
                        TimeOffset = kf.InHandle.TimeOffset,
                        ValueOffset = kf.InHandle.ValueOffset
                    }
                    : null,
                OutHandle = kf.OutHandle != null
                    ? new BezierHandleData
                    {
                        TimeOffset = kf.OutHandle.TimeOffset,
                        ValueOffset = kf.OutHandle.ValueOffset
                    }
                    : null
            });
        }

        return dto;
    }

    private static void ApplyKeyframeSystemData(KeyframeSystemData data, KeyframeSystem system)
    {
        system.ClearKeyframes();

        foreach (var kfData in data.Keyframes)
        {
            var keyframe = new Keyframe(kfData.Time, kfData.Value, kfData.Interpolation);

            if (kfData.InHandle != null)
            {
                keyframe.InHandle = new BezierHandle(kfData.InHandle.TimeOffset, kfData.InHandle.ValueOffset);
            }

            if (kfData.OutHandle != null)
            {
                keyframe.OutHandle = new BezierHandle(kfData.OutHandle.TimeOffset, kfData.OutHandle.ValueOffset);
            }

            system.Keyframes.Add(keyframe);
        }

        system.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    public void Dispose()
    {
        _renderService.Dispose();
        _timelineService.Dispose();
    }
}

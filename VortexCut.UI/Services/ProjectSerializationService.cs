using VortexCut.Core.Models;
using VortexCut.Core.Serialization;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Services;

/// <summary>
/// 프로젝트 직렬화 서비스 — Extract/Restore + DTO 변환
/// ProjectService의 런타임 로직과 분리
/// </summary>
public class ProjectSerializationService
{
    private readonly ProjectService _projectService;

    public ProjectSerializationService(ProjectService projectService)
    {
        _projectService = projectService;
    }

    /// <summary>
    /// 현재 UI 상태로부터 ProjectData DTO를 생성 (저장용).
    /// </summary>
    public ProjectData ExtractProjectData(MainViewModel mainVm)
    {
        var currentProject = _projectService.CurrentProject;
        if (currentProject == null)
        {
            throw new InvalidOperationException("No project is open");
        }

        var timelineVm = mainVm.Timeline;
        var data = new ProjectData
        {
            ProjectName = mainVm.ProjectName,
            Width = currentProject.Width,
            Height = currentProject.Height,
            Fps = currentProject.Fps,
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
        var timelineService = _projectService.TimelineServiceInternal;
        var renderService = _projectService.RenderServiceInternal;

        // 1) 기존 렌더러/타임라인 정리
        renderService.DestroyRenderer();
        timelineService.DestroyTimeline();

        // 2) Project / Timeline 재생성
        _projectService.CurrentProjectInternal = new Project(data.ProjectName, data.Width, data.Height, data.Fps);
        timelineService.CreateTimeline(data.Width, data.Height, data.Fps);

        _projectService.TimelineHandleInternal = timelineService.GetTimelineHandle();
        renderService.CreateRenderer(_projectService.TimelineHandleInternal!.DangerousGetHandle());

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
            var trackId = timelineService.AddVideoTrack();
            if (i == 0)
            {
                _projectService.DefaultVideoTrackIdInternal = trackId;
            }

            var model = DtoToTrack(dto, trackId, TrackType.Video);
            timelineVm.VideoTracks.Add(model);

            combinedIndexToTrackId[combinedIndex++] = trackId;
        }

        // 오디오 트랙
        for (int i = 0; i < data.AudioTracks.Count; i++)
        {
            var dto = data.AudioTracks[i];
            var trackId = timelineService.AddAudioTrack();
            var model = DtoToTrack(dto, trackId, TrackType.Audio);
            timelineVm.AudioTracks.Add(model);

            combinedIndexToTrackId[combinedIndex++] = trackId;
        }

        // 자막 트랙 복원 (Rust에 등록하지 않음 — C#만)
        for (int i = 0; i < data.SubtitleTracks.Count; i++)
        {
            var dto = data.SubtitleTracks[i];
            var model = DtoToTrack(dto, (ulong)(1000 + i), TrackType.Subtitle);
            timelineVm.SubtitleTracks.Add(model);
            combinedIndex++;
        }

        // 6) Clips 복원 (Rust Timeline + Project + ViewModel)
        var currentProject = _projectService.CurrentProjectInternal!;
        currentProject.Clips.Clear();
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
                trackId = _projectService.DefaultVideoTrackIdInternal;
            }

            // 비디오/오디오 트랙 구분 (combined index 기준)
            bool isAudioClip = clipDto.TrackIndex >= data.VideoTracks.Count;

            ulong clipId;
            if (isAudioClip)
            {
                clipId = timelineService.AddAudioClip(trackId, clipDto.FilePath, clipDto.StartTimeMs, clipDto.DurationMs);
            }
            else
            {
                clipId = timelineService.AddVideoClip(trackId, clipDto.FilePath, clipDto.StartTimeMs, clipDto.DurationMs);
            }

            var clipModel = DtoToClip(clipDto, clipId);
            currentProject.Clips.Add(clipModel);
            timelineVm.Clips.Add(clipModel);

            // 이펙트가 있으면 Rust Renderer에 전달
            if (Math.Abs(clipModel.Brightness) > 0.001 || Math.Abs(clipModel.Contrast) > 0.001
                || Math.Abs(clipModel.Saturation) > 0.001 || Math.Abs(clipModel.Temperature) > 0.001)
            {
                try
                {
                    renderService.SetClipEffects(clipId,
                        (float)clipModel.Brightness, (float)clipModel.Contrast,
                        (float)clipModel.Saturation, (float)clipModel.Temperature);
                }
                catch { /* Renderer busy 시 무시 */ }
            }

            // 볼륨/속도/페이드가 기본값이 아니면 Rust Timeline에 전달
            if (Math.Abs(clipModel.Volume - 1.0) > 0.001)
                _projectService.SetClipVolume(clipId, (float)clipModel.Volume);
            if (Math.Abs(clipModel.Speed - 1.0) > 0.001)
                _projectService.SetClipSpeed(clipId, clipModel.Speed);
            if (clipModel.FadeInMs > 0 || clipModel.FadeOutMs > 0)
                _projectService.SetClipFade(clipId, clipModel.FadeInMs, clipModel.FadeOutMs);
            if (clipModel.TransitionType != TransitionType.None)
                _projectService.SetClipTransition(clipId, clipModel.TransitionType);
        }

        // 7) Markers 복원 (ViewModel만)
        timelineVm.Markers.Clear();
        foreach (var markerDto in data.Markers)
        {
            var marker = DtoToMarker(markerDto);
            timelineVm.Markers.Add(marker);
        }
    }

    // ==================== DTO 변환 메서드 ====================

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
            TransitionType = clip.TransitionType,
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
            FadeOutMs = data.FadeOutMs,
            TransitionType = data.TransitionType
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
}

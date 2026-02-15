using VortexCut.Core.Interfaces;
using VortexCut.Core.Models;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// Inspector 패널 ViewModel — 색보정, 오디오, 트랜지션 비즈니스 로직
/// </summary>
public class InspectorViewModel
{
    private readonly IProjectService _projectService;
    private readonly PreviewViewModel _preview;
    private readonly TimelineViewModel _timeline;

    public InspectorViewModel(IProjectService projectService, PreviewViewModel preview, TimelineViewModel timeline)
    {
        _projectService = projectService;
        _preview = preview;
        _timeline = timeline;
    }

    // ==================== Properties (Editing) ====================

    /// <summary>
    /// Properties 슬라이더 값 적용 (StartTime, Duration, Opacity → 모델 + FFI)
    /// </summary>
    public void ApplyPropertiesChange(ClipModel clip, long startTimeMs, long durationMs, double opacityPercent)
    {
        clip.StartTimeMs = startTimeMs;
        clip.DurationMs = durationMs;
        _projectService.SyncClipToRust(clip);

        // Opacity → 키프레임 첫 번째 값으로 설정 (간이 편집)
        var opacityValue = opacityPercent / 100.0;
        if (clip.OpacityKeyframes.Keyframes.Count == 0)
        {
            clip.OpacityKeyframes.AddKeyframe(0, opacityValue);
        }
        else
        {
            var kf = clip.OpacityKeyframes.Keyframes[0];
            clip.OpacityKeyframes.UpdateKeyframe(kf, kf.Time, opacityValue);
        }
    }

    /// <summary>
    /// Properties 초기화 (Opacity 리셋)
    /// </summary>
    public void ResetProperties(ClipModel clip)
    {
        if (clip.OpacityKeyframes.Keyframes.Count > 0)
        {
            var kf = clip.OpacityKeyframes.Keyframes[0];
            clip.OpacityKeyframes.UpdateKeyframe(kf, kf.Time, 1.0);
        }
    }

    // ==================== Color ====================

    /// <summary>
    /// 색보정 이펙트 적용 (슬라이더 → 모델 + FFI + 렌더)
    /// </summary>
    public void ApplyColorEffects(ClipModel clip, double brightness, double contrast, double saturation, double temperature)
    {
        clip.Brightness = brightness;
        clip.Contrast = contrast;
        clip.Saturation = saturation;
        clip.Temperature = temperature;

        _projectService.SetClipEffects(
            clip.Id,
            (float)brightness,
            (float)contrast,
            (float)saturation,
            (float)temperature);

        _preview.RenderFrameAsync(_timeline.CurrentTimeMs);
    }

    /// <summary>
    /// 색보정 초기화
    /// </summary>
    public void ResetColorEffects(ClipModel clip)
    {
        clip.Brightness = 0;
        clip.Contrast = 0;
        clip.Saturation = 0;
        clip.Temperature = 0;

        _projectService.SetClipEffects(clip.Id, 0, 0, 0, 0);
        _preview.RenderFrameAsync(_timeline.CurrentTimeMs);
    }

    // ==================== Audio ====================

    /// <summary>
    /// 오디오 설정 적용 (슬라이더 → 모델 + FFI + 렌더)
    /// </summary>
    public void ApplyAudioSettings(ClipModel clip, double volume, double speed, long fadeInMs, long fadeOutMs)
    {
        clip.Volume = volume;
        clip.Speed = speed;
        clip.FadeInMs = fadeInMs;
        clip.FadeOutMs = fadeOutMs;

        _projectService.SetClipVolume(clip.Id, (float)volume);
        _projectService.SetClipSpeed(clip.Id, speed);
        _projectService.SetClipFade(clip.Id, fadeInMs, fadeOutMs);

        _projectService.ClearRenderCache();
        _preview.RenderFrameAsync(_timeline.CurrentTimeMs);
    }

    /// <summary>
    /// 오디오 설정 초기화
    /// </summary>
    public void ResetAudioSettings(ClipModel clip)
    {
        clip.Volume = 1.0;
        clip.Speed = 1.0;
        clip.FadeInMs = 0;
        clip.FadeOutMs = 0;

        _projectService.SetClipVolume(clip.Id, 1.0f);
        _projectService.SetClipSpeed(clip.Id, 1.0);
        _projectService.SetClipFade(clip.Id, 0, 0);

        _projectService.ClearRenderCache();
        _preview.RenderFrameAsync(_timeline.CurrentTimeMs);
    }

    // ==================== Subtitle ====================

    /// <summary>
    /// 자막 텍스트 적용
    /// </summary>
    public void ApplySubtitleText(SubtitleClipModel clip, string text)
    {
        clip.Text = text;
        _preview.RefreshSubtitleOverlay();
    }

    /// <summary>
    /// 자막 스타일 적용 (폰트 크기, 위치, Bold, Italic)
    /// </summary>
    public void ApplySubtitleStyle(SubtitleClipModel clip, double fontSize, SubtitlePosition position, bool isBold, bool isItalic)
    {
        clip.Style.FontSize = fontSize;
        clip.Style.Position = position;
        clip.Style.IsBold = isBold;
        clip.Style.IsItalic = isItalic;
        _preview.RefreshSubtitleOverlay();
    }

    /// <summary>
    /// 자막 스타일 초기화
    /// </summary>
    public void ResetSubtitleStyle(SubtitleClipModel clip)
    {
        clip.Style.FontSize = 48;
        clip.Style.Position = SubtitlePosition.Bottom;
        clip.Style.IsBold = false;
        clip.Style.IsItalic = false;
        _preview.RefreshSubtitleOverlay();
    }

    // ==================== Transition ====================

    /// <summary>
    /// 트랜지션 타입 적용
    /// </summary>
    public void ApplyTransition(ClipModel clip, TransitionType transitionType)
    {
        clip.TransitionType = transitionType;

        _projectService.SetClipTransition(clip.Id, transitionType);
        _projectService.ClearRenderCache();
        _preview.RenderFrameAsync(_timeline.CurrentTimeMs);
    }

    /// <summary>
    /// 트랜지션 초기화
    /// </summary>
    public void ResetTransition(ClipModel clip)
    {
        clip.TransitionType = TransitionType.None;

        _projectService.SetClipTransition(clip.Id, TransitionType.None);
        _projectService.ClearRenderCache();
        _preview.RenderFrameAsync(_timeline.CurrentTimeMs);
    }
}

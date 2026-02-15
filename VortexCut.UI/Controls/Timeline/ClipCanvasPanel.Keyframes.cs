using Avalonia;
using Avalonia.Media;
using System.Linq;
using VortexCut.Core.Models;
using VortexCut.UI.Services;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// ClipCanvasPanel — 키프레임 렌더링 + 링크된 클립 연결선
/// </summary>
public partial class ClipCanvasPanel
{
    private void DrawKeyframes(DrawingContext context, ClipModel clip)
    {
        if (_viewModel == null) return;

        var keyframeSystem = GetKeyframeSystem(clip, _viewModel.SelectedKeyframeSystem);
        if (keyframeSystem == null || keyframeSystem.Keyframes.Count == 0) return;

        double clipX = TimeToX(clip.StartTimeMs);
        double clipY = GetTrackYPosition(clip.TrackIndex);
        double keyframeY = clipY + 20;

        // 키프레임 간 연결선 (After Effects 스타일)
        if (keyframeSystem.Keyframes.Count > 1)
        {
            var sortedKeyframes = keyframeSystem.Keyframes.OrderBy(k => k.Time).ToList();

            for (int i = 0; i < sortedKeyframes.Count - 1; i++)
            {
                var kf1 = sortedKeyframes[i];
                var kf2 = sortedKeyframes[i + 1];

                double kf1X = clipX + (kf1.Time * 1000 * _pixelsPerMs);
                double kf2X = clipX + (kf2.Time * 1000 * _pixelsPerMs);

                var curveGeometry = new StreamGeometry();
                using (var ctx = curveGeometry.Open())
                {
                    ctx.BeginFigure(new Point(kf1X, keyframeY), false);

                    if (kf1.Interpolation == InterpolationType.Linear || kf1.Interpolation == InterpolationType.Hold)
                    {
                        ctx.LineTo(new Point(kf2X, keyframeY));
                    }
                    else
                    {
                        double midX = (kf1X + kf2X) / 2;
                        double controlY = keyframeY - 8;

                        ctx.QuadraticBezierTo(
                            new Point(midX, controlY),
                            new Point(kf2X, keyframeY));
                    }
                }

                context.DrawGeometry(null, RenderResourceCache.KeyframeShadowPen, curveGeometry);
                context.DrawGeometry(null, RenderResourceCache.KeyframeLinePen, curveGeometry);
            }
        }

        // 키프레임 다이아몬드 (연결선 위에 렌더링)
        foreach (var keyframe in keyframeSystem.Keyframes)
        {
            double keyframeTimeMs = keyframe.Time * 1000;
            double keyframeX = clipX + (keyframeTimeMs * _pixelsPerMs);
            DrawKeyframeDiamond(context, keyframeX, keyframeY, keyframe.Interpolation);
        }
    }

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

    private void DrawKeyframeDiamond(DrawingContext context, double x, double y, InterpolationType interpolation)
    {
        const double Size = 10;

        Color color = interpolation switch
        {
            InterpolationType.Linear => Color.FromRgb(255, 220, 80),
            InterpolationType.Bezier => Color.FromRgb(80, 220, 255),
            InterpolationType.EaseIn => Color.FromRgb(120, 255, 120),
            InterpolationType.EaseOut => Color.FromRgb(120, 180, 255),
            InterpolationType.EaseInOut => Color.FromRgb(255, 180, 80),
            InterpolationType.Hold => Color.FromRgb(255, 100, 100),
            _ => Color.FromRgb(255, 220, 80)
        };

        // 다이아몬드 그림자
        var shadowGeometry = new StreamGeometry();
        using (var ctx = shadowGeometry.Open())
        {
            ctx.BeginFigure(new Point(x + 1, y - Size / 2 + 1), true);
            ctx.LineTo(new Point(x + Size / 2 + 1, y + 1));
            ctx.LineTo(new Point(x + 1, y + Size / 2 + 1));
            ctx.LineTo(new Point(x - Size / 2 + 1, y + 1));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(RenderResourceCache.PlayheadShadowBrush, null, shadowGeometry);

        // 다이아몬드 본체 (그라디언트)
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, y - Size / 2), true);
            ctx.LineTo(new Point(x + Size / 2, y));
            ctx.LineTo(new Point(x, y + Size / 2));
            ctx.LineTo(new Point(x - Size / 2, y));
            ctx.EndFigure(true);
        }

        var darkerColor = Color.FromRgb(
            (byte)Math.Max(0, color.R - 60),
            (byte)Math.Max(0, color.G - 60),
            (byte)Math.Max(0, color.B - 60));
        var diamondGradient = RenderResourceCache.GetVerticalGradient(color, darkerColor);

        context.DrawGeometry(diamondGradient, RenderResourceCache.DiamondBorderPen, geometry);

        // 내부 하이라이트 (반짝임 효과)
        var highlightGeometry = new StreamGeometry();
        using (var ctx = highlightGeometry.Open())
        {
            ctx.BeginFigure(new Point(x, y - Size / 2 + 2), false);
            ctx.LineTo(new Point(x + Size / 4, y - Size / 4 + 1));
        }
        context.DrawGeometry(null, RenderResourceCache.DiamondHighlightPen, highlightGeometry);
    }

    /// <summary>
    /// 링크된 클립 연결선 렌더링 (비디오+오디오 링크 표시)
    /// </summary>
    private void DrawLinkedClipConnections(DrawingContext context)
    {
        long visibleStartMs = XToTime(-50);
        long visibleEndMs = XToTime(Bounds.Width + 50);

        var linkedVideoClips = _clips.Where(c => c.LinkedAudioClipId.HasValue).ToList();

        foreach (var videoClip in linkedVideoClips)
        {
            long videoEndMs = videoClip.StartTimeMs + videoClip.DurationMs;
            if (videoEndMs < visibleStartMs || videoClip.StartTimeMs > visibleEndMs)
                continue;

            var audioClip = _clips.FirstOrDefault(c => c.Id == videoClip.LinkedAudioClipId);
            if (audioClip == null) continue;

            double videoX = TimeToX(videoClip.StartTimeMs) + DurationToWidth(videoClip.DurationMs) / 2;
            double videoY = GetTrackYPosition(videoClip.TrackIndex);
            var videoTrack = GetTrackByIndex(videoClip.TrackIndex);
            if (videoTrack == null) continue;
            double videoHeight = videoTrack.Height - 10;
            double videoCenterY = videoY + videoHeight / 2 + 5;

            double audioX = TimeToX(audioClip.StartTimeMs) + DurationToWidth(audioClip.DurationMs) / 2;
            double audioY = GetTrackYPosition(audioClip.TrackIndex);
            var audioTrack = GetTrackByIndex(audioClip.TrackIndex);
            if (audioTrack == null) continue;
            double audioHeight = audioTrack.Height - 10;
            double audioCenterY = audioY + audioHeight / 2 + 5;

            context.DrawLine(RenderResourceCache.LinkLinePen,
                new Point(videoX, videoCenterY),
                new Point(audioX, audioCenterY));

            var videoIconRect = new Rect(videoX - 4, videoCenterY - 4, 8, 8);
            context.FillRectangle(RenderResourceCache.LinkBrush, videoIconRect);
            context.DrawRectangle(RenderResourceCache.LinkNodeBorderPen, videoIconRect);

            var audioIconRect = new Rect(audioX - 4, audioCenterY - 4, 8, 8);
            context.FillRectangle(RenderResourceCache.LinkBrush, audioIconRect);
            context.DrawRectangle(RenderResourceCache.LinkNodeBorderPen, audioIconRect);
        }
    }

    /// <summary>
    /// 마우스 위치에서 키프레임 검색 (HitTest)
    /// </summary>
    private (Keyframe?, KeyframeSystem?, ClipModel?) GetKeyframeAtPosition(Point point)
    {
        if (_viewModel == null) return (null, null, null);

        foreach (var clip in _viewModel.SelectedClips)
        {
            var keyframeSystem = GetKeyframeSystem(clip, _viewModel.SelectedKeyframeSystem);
            if (keyframeSystem == null) continue;

            double clipX = TimeToX(clip.StartTimeMs);
            double clipY = GetTrackYPosition(clip.TrackIndex);
            double keyframeY = clipY + 20;

            foreach (var keyframe in keyframeSystem.Keyframes)
            {
                double keyframeTimeMs = keyframe.Time * 1000;
                double keyframeX = clipX + (keyframeTimeMs * _pixelsPerMs);

                if (Math.Abs(point.X - keyframeX) < 10 && Math.Abs(point.Y - keyframeY) < 10)
                    return (keyframe, keyframeSystem, clip);
            }
        }

        return (null, null, null);
    }
}

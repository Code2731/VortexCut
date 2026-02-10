using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;
using VortexCut.UI.Services;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 클립 엣지 (트림용)
/// </summary>
public enum ClipEdge { None, Left, Right }

/// <summary>
/// 클립 렌더링 영역 (드래그, 선택, Snap 처리)
/// </summary>
public class ClipCanvasPanel : Control
{
    private const double TrackHeight = 60;
    private const double MinClipWidth = 20;

    private TimelineViewModel? _viewModel;
    private SnapService? _snapService;
    private List<ClipModel> _clips = new();
    private List<TrackModel> _videoTracks = new();
    private List<TrackModel> _audioTracks = new();
    private double _pixelsPerMs = 0.1;
    private double _scrollOffsetX = 0;
    private ClipModel? _selectedClip;
    private ClipModel? _draggingClip;
    private ClipModel? _hoveredClip;  // 호버된 클립
    private Point _dragStartPoint;
    private bool _isDragging;
    private bool _isPanning;
    private Point _panStartPoint;
    private long _lastSnappedTimeMs = -1;
    private bool _isTrimming;
    private ClipEdge _trimEdge = ClipEdge.None;
    private long _originalStartTimeMs;
    private long _originalDurationMs;

    // 키프레임 드래그 상태
    private Keyframe? _draggingKeyframe;
    private KeyframeSystem? _draggingKeyframeSystem;
    private ClipModel? _draggingKeyframeClip;
    private bool _isDraggingKeyframe;

    public ClipCanvasPanel()
    {
        ClipToBounds = true;
        Focusable = true;
        DragDrop.SetAllowDrop(this, true);

        AddHandler(DragDrop.DragOverEvent, HandleDragOver);
        AddHandler(DragDrop.DropEvent, HandleDrop);
    }

    public void SetViewModel(TimelineViewModel viewModel)
    {
        _viewModel = viewModel;
        _snapService = new SnapService(viewModel);
    }

    public void SetClips(IEnumerable<ClipModel> clips)
    {
        _clips = new List<ClipModel>(clips);
        InvalidateVisual();
    }

    public void SetTracks(List<TrackModel> videoTracks, List<TrackModel> audioTracks)
    {
        _videoTracks = videoTracks;
        _audioTracks = audioTracks;
        InvalidateVisual();
    }

    public void SetZoom(double pixelsPerMs)
    {
        _pixelsPerMs = Math.Clamp(pixelsPerMs, 0.01, 1.0);
        InvalidateVisual();
    }

    public void SetScrollOffset(double offsetX)
    {
        _scrollOffsetX = offsetX;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 배경
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1E1E1E")), Bounds);

        // 트랙 배경
        DrawTrackBackgrounds(context);

        // Snap 가이드라인 (드래그 중일 때)
        if (_isDragging && _lastSnappedTimeMs >= 0)
        {
            DrawSnapGuideline(context, _lastSnappedTimeMs);
        }

        // 클립들
        DrawClips(context);

        // 링크된 클립 연결선 (비디오+오디오)
        DrawLinkedClipConnections(context);

        // Playhead
        DrawPlayhead(context);

        // 호버된 클립 툴팁
        if (_hoveredClip != null)
        {
            DrawClipTooltip(context, _hoveredClip);
        }
    }

    private void DrawTrackBackgrounds(DrawingContext context)
    {
        var borderPen = new Pen(new SolidColorBrush(Color.Parse("#3A3A3C")), 0.8);

        // 비디오 트랙
        for (int i = 0; i < _videoTracks.Count; i++)
        {
            var track = _videoTracks[i];
            double y = i * track.Height;
            var trackRect = new Rect(0, y, Bounds.Width, track.Height);

            // 프로페셔널 그라디언트 배경 (교차 패턴)
            var isEven = i % 2 == 0;
            var trackGradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(isEven ? Color.Parse("#2D2D30") : Color.Parse("#252527"), 0),
                    new GradientStop(isEven ? Color.Parse("#252527") : Color.Parse("#1E1E20"), 1)
                }
            };

            context.FillRectangle(trackGradient, trackRect);

            // 미묘한 상단 하이라이트 (3D 효과)
            if (i > 0)
            {
                var highlightPen = new Pen(
                    new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    1);
                context.DrawLine(highlightPen,
                    new Point(0, y),
                    new Point(Bounds.Width, y));
            }

            context.DrawRectangle(borderPen, trackRect);
        }

        // 오디오 트랙
        double audioStartY = _videoTracks.Sum(t => t.Height);
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            var track = _audioTracks[i];
            double y = audioStartY + i * track.Height;
            var trackRect = new Rect(0, y, Bounds.Width, track.Height);

            // 오디오 트랙은 약간 다른 색조 (미묘한 초록 톤)
            var isEven = i % 2 == 0;
            var audioTrackGradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(isEven ? Color.Parse("#252828") : Color.Parse("#1E2120"), 0),
                    new GradientStop(isEven ? Color.Parse("#1E2120") : Color.Parse("#181A18"), 1)
                }
            };

            context.FillRectangle(audioTrackGradient, trackRect);

            // 미묘한 상단 하이라이트
            if (i > 0)
            {
                var highlightPen = new Pen(
                    new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    1);
                context.DrawLine(highlightPen,
                    new Point(0, y),
                    new Point(Bounds.Width, y));
            }

            context.DrawRectangle(borderPen, trackRect);
        }
    }

    private void DrawClips(DrawingContext context)
    {
        foreach (var clip in _clips)
        {
            bool isSelected = _viewModel?.SelectedClips.Contains(clip) ?? false;
            bool isHovered = clip == _hoveredClip;
            DrawClip(context, clip, isSelected, isHovered);
        }
    }

    private void DrawClip(DrawingContext context, ClipModel clip, bool isSelected, bool isHovered)
    {
        double x = TimeToX(clip.StartTimeMs);
        double width = DurationToWidth(clip.DurationMs);

        // 트랙 Y 위치 계산
        double y = GetTrackYPosition(clip.TrackIndex);
        var track = GetTrackByIndex(clip.TrackIndex);
        if (track == null) return;

        double height = track.Height - 10;
        var clipRect = new Rect(x, y + 5, Math.Max(width, MinClipWidth), height);

        // 클립 타입 감지 (비디오/오디오)
        bool isAudioClip = track.Type == TrackType.Audio;

        // 클립 배경 (그라데이션 - DaVinci Resolve 스타일)
        Color topColor, bottomColor;

        if (isAudioClip)
        {
            // 오디오 클립: 초록색 그라데이션
            topColor = isSelected
                ? Color.Parse("#5CB85C")  // 밝은 초록
                : Color.Parse("#3A5A3A");  // 다크 초록
            bottomColor = isSelected
                ? Color.Parse("#449D44")  // 어두운 초록
                : Color.Parse("#2A4A2A");  // 더 어두운 초록
        }
        else
        {
            // 비디오 클립: 파란색 그라데이션
            topColor = isSelected
                ? Color.Parse("#4A90E2")  // 밝은 파란색
                : Color.Parse("#3A5A7A");  // 다크 블루
            bottomColor = isSelected
                ? Color.Parse("#2D6AA6")  // 어두운 파란색
                : Color.Parse("#2A4A6A");  // 더 어두운 블루
        }

        var gradientBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(topColor, 0),
                new GradientStop(bottomColor, 1)
            }
        };

        // 클립 그림자 (DaVinci Resolve 스타일)
        var shadowRect = new Rect(
            clipRect.X + 2,
            clipRect.Y + 2,
            clipRect.Width,
            clipRect.Height);
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            shadowRect);

        context.FillRectangle(gradientBrush, clipRect);

        // 선택된 클립 글로우 효과
        if (isSelected)
        {
            var glowRect1 = new Rect(
                clipRect.X - 3,
                clipRect.Y - 3,
                clipRect.Width + 6,
                clipRect.Height + 6);
            var glowBrush1 = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            context.FillRectangle(glowBrush1, glowRect1);

            var glowRect2 = new Rect(
                clipRect.X - 1,
                clipRect.Y - 1,
                clipRect.Width + 2,
                clipRect.Height + 2);
            var glowBrush2 = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            context.FillRectangle(glowBrush2, glowRect2);
        }

        // 호버 효과 (선택되지 않은 클립만)
        if (isHovered && !isSelected)
        {
            var hoverRect = new Rect(
                clipRect.X - 1,
                clipRect.Y - 1,
                clipRect.Width + 2,
                clipRect.Height + 2);
            var hoverBrush = new SolidColorBrush(Color.FromArgb(40, 0, 122, 204)); // 미묘한 파란색
            context.FillRectangle(hoverBrush, hoverRect);
        }

        // 오디오 웨이브폼 (간단한 시뮬레이션)
        if (isAudioClip && width > 50)
        {
            DrawAudioWaveform(context, clipRect);
        }

        // 테두리 (선택된 클립은 밝은 하얀색, 일반은 미묘한 회색)
        var borderPen = isSelected
            ? new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2.5)
            : new Pen(new SolidColorBrush(Color.Parse("#5A5A5C")), 1.2);

        context.DrawRectangle(borderPen, clipRect);

        // 트림 핸들 시각화 (양 끝 10px 영역)
        if (isSelected && width > 30)
        {
            // 왼쪽 트림 핸들
            var leftHandleRect = new Rect(clipRect.X, clipRect.Y, 2, clipRect.Height);
            context.FillRectangle(
                new SolidColorBrush(Color.FromRgb(255, 200, 80)),
                leftHandleRect);

            // 오른쪽 트림 핸들
            var rightHandleRect = new Rect(
                clipRect.Right - 2,
                clipRect.Y,
                2,
                clipRect.Height);
            context.FillRectangle(
                new SolidColorBrush(Color.FromRgb(255, 200, 80)),
                rightHandleRect);
        }

        // 클립 타입 아이콘 (좌측 상단)
        if (width > 30)
        {
            var iconText = isAudioClip ? "🔊" : "🎬";
            var iconFormatted = new FormattedText(
                iconText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal),
                14,
                Brushes.White);

            // 아이콘 배경 (작은 원형 배지)
            var iconBgRect = new Rect(x + 4, y + 4, 20, 20);
            var iconBgBrush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromArgb(200, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(150, 0, 0, 0), 1)
                }
            };
            context.FillRectangle(iconBgBrush, iconBgRect);
            context.DrawText(iconFormatted, new Point(x + 7, y + 5));
        }

        // 클립 이름 (가독성 개선)
        if (width > 40) // 너무 좁은 클립은 텍스트 생략
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(clip.FilePath);
            if (fileName.Length > 20)
                fileName = fileName.Substring(0, 17) + "...";

            var text = new FormattedText(
                fileName,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold),
                12,
                Brushes.White);

            // 텍스트 배경 (더 선명한 그라디언트)
            var textBgRect = new Rect(x + 28, y + 6, text.Width + 8, text.Height + 6);
            var textBgGradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromArgb(200, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.8),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
                }
            };
            context.FillRectangle(textBgGradient, textBgRect);

            // 텍스트
            context.DrawText(text, new Point(x + 32, y + 9));

            // 클립 지속시간 표시 (우측 상단 - 더 선명하게)
            if (width > 100)
            {
                var duration = TimeSpan.FromMilliseconds(clip.DurationMs);
                var durationText = duration.ToString(@"mm\:ss");
                var durationFormatted = new FormattedText(
                    durationText,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold),
                    11,
                    new SolidColorBrush(Color.FromRgb(255, 220, 80)));

                var durationX = x + width - durationFormatted.Width - 10;
                var durationBgRect = new Rect(durationX - 4, y + 6, durationFormatted.Width + 8, durationFormatted.Height + 6);
                var durationBgGradient = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = new GradientStops
                    {
                        new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                        new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.2),
                        new GradientStop(Color.FromArgb(200, 0, 0, 0), 1)
                    }
                };
                context.FillRectangle(durationBgGradient, durationBgRect);
                context.DrawText(durationFormatted, new Point(durationX, y + 9));
            }
        }

        // 키프레임 렌더링 (선택된 클립만)
        if (isSelected && _viewModel != null)
        {
            DrawKeyframes(context, clip);
        }
    }

    /// <summary>
    /// 오디오 웨이브폼 렌더링 (DaVinci Resolve 스타일 - 고밀도)
    /// </summary>
    private void DrawAudioWaveform(DrawingContext context, Rect clipRect)
    {
        const int SampleInterval = 2; // 2픽셀마다 샘플 (고밀도)
        const double MaxAmplitude = 0.42; // 클립 높이의 42%

        var centerY = clipRect.Top + clipRect.Height / 2;
        var random = new System.Random((int)clipRect.X); // 일관된 랜덤 시드

        // DaVinci Resolve 스타일 수직 바 렌더링
        for (double x = clipRect.Left; x < clipRect.Right; x += SampleInterval)
        {
            // 복잡한 웨이브 생성 (여러 주파수 조합 - 사실적인 오디오 시뮬레이션)
            double phase1 = (x - clipRect.Left) / 15.0;
            double phase2 = (x - clipRect.Left) / 35.0;
            double phase3 = (x - clipRect.Left) / 50.0;

            double sine1 = Math.Sin(phase1) * 0.4;
            double sine2 = Math.Sin(phase2) * 0.3;
            double sine3 = Math.Sin(phase3) * 0.2;
            double noise = (random.NextDouble() - 0.5) * 0.6;

            double combinedWave = (sine1 + sine2 + sine3 + noise) / 2.0;
            double amplitude = Math.Abs(combinedWave) * MaxAmplitude * clipRect.Height;

            // 수직 바 그리기 (그라데이션 효과)
            var topY = centerY - amplitude;
            var bottomY = centerY + amplitude;

            // 밝은 초록색 (DaVinci Resolve 스타일)
            var pen = new Pen(
                new SolidColorBrush(Color.FromArgb(200, 130, 230, 130)),
                1.4);

            context.DrawLine(pen,
                new Point(x, topY),
                new Point(x, bottomY));
        }

        // 중앙선 (가이드라인)
        var centerLinePen = new Pen(
            new SolidColorBrush(Color.FromArgb(70, 160, 255, 160)),
            0.6);
        context.DrawLine(centerLinePen,
            new Point(clipRect.Left, centerY),
            new Point(clipRect.Right, centerY));
    }

    private void DrawKeyframes(DrawingContext context, ClipModel clip)
    {
        if (_viewModel == null) return;

        var keyframeSystem = GetKeyframeSystem(clip, _viewModel.SelectedKeyframeSystem);
        if (keyframeSystem == null || keyframeSystem.Keyframes.Count == 0) return;

        double clipX = TimeToX(clip.StartTimeMs);
        double clipY = GetTrackYPosition(clip.TrackIndex);
        double keyframeY = clipY + 20; // 클립 상단에서 20px

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

                // 곡선 연결선 (베지어 곡선 시뮬레이션)
                var curveGeometry = new StreamGeometry();
                using (var ctx = curveGeometry.Open())
                {
                    ctx.BeginFigure(new Point(kf1X, keyframeY), false);

                    // 보간 타입에 따라 다른 곡선
                    if (kf1.Interpolation == InterpolationType.Linear || kf1.Interpolation == InterpolationType.Hold)
                    {
                        // 직선
                        ctx.LineTo(new Point(kf2X, keyframeY));
                    }
                    else
                    {
                        // 부드러운 베지어 곡선 (EaseIn, EaseOut, EaseInOut, Bezier)
                        double midX = (kf1X + kf2X) / 2;
                        double controlY = keyframeY - 8; // 위로 8px 올림

                        ctx.QuadraticBezierTo(
                            new Point(midX, controlY),
                            new Point(kf2X, keyframeY));
                    }
                }

                // 연결선 그림자
                var lineShadowPen = new Pen(
                    new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                    2);
                context.DrawGeometry(null, lineShadowPen, curveGeometry);

                // 연결선 본체 (밝은 시안색)
                var linePen = new Pen(
                    new SolidColorBrush(Color.FromArgb(180, 80, 220, 255)),
                    1.5);
                context.DrawGeometry(null, linePen, curveGeometry);
            }
        }

        // 키프레임 다이아몬드 (연결선 위에 렌더링)
        foreach (var keyframe in keyframeSystem.Keyframes)
        {
            double keyframeTimeMs = keyframe.Time * 1000; // 초 → ms
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

        // 보간 타입에 따라 색상 변경 (더 밝고 선명하게)
        Color color = interpolation switch
        {
            InterpolationType.Linear => Color.FromRgb(255, 220, 80),    // 밝은 황금색
            InterpolationType.Bezier => Color.FromRgb(80, 220, 255),    // 밝은 시안
            InterpolationType.EaseIn => Color.FromRgb(120, 255, 120),   // 밝은 초록
            InterpolationType.EaseOut => Color.FromRgb(120, 180, 255),  // 밝은 파랑
            InterpolationType.EaseInOut => Color.FromRgb(255, 180, 80), // 밝은 주황
            InterpolationType.Hold => Color.FromRgb(255, 100, 100),     // 밝은 빨강
            _ => Color.FromRgb(255, 220, 80)
        };

        // 다이아몬드 그림자 (깊이감)
        var shadowGeometry = new StreamGeometry();
        using (var ctx = shadowGeometry.Open())
        {
            ctx.BeginFigure(new Point(x + 1, y - Size / 2 + 1), true);
            ctx.LineTo(new Point(x + Size / 2 + 1, y + 1));
            ctx.LineTo(new Point(x + 1, y + Size / 2 + 1));
            ctx.LineTo(new Point(x - Size / 2 + 1, y + 1));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            null,
            shadowGeometry);

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

        var diamondGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(color, 0),
                new GradientStop(Color.FromRgb(
                    (byte)Math.Max(0, color.R - 60),
                    (byte)Math.Max(0, color.G - 60),
                    (byte)Math.Max(0, color.B - 60)), 1)
            }
        };

        context.DrawGeometry(
            diamondGradient,
            new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1.5),
            geometry);

        // 내부 하이라이트 (반짝임 효과)
        var highlightGeometry = new StreamGeometry();
        using (var ctx = highlightGeometry.Open())
        {
            ctx.BeginFigure(new Point(x, y - Size / 2 + 2), false);
            ctx.LineTo(new Point(x + Size / 4, y - Size / 4 + 1));
        }
        context.DrawGeometry(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.2),
            highlightGeometry);
    }

    /// <summary>
    /// 링크된 클립 연결선 렌더링 (비디오+오디오 링크 표시)
    /// </summary>
    private void DrawLinkedClipConnections(DrawingContext context)
    {
        // 비디오 클립 중 LinkedAudioClipId가 있는 클립 찾기
        var linkedVideoClips = _clips.Where(c => c.LinkedAudioClipId.HasValue).ToList();

        foreach (var videoClip in linkedVideoClips)
        {
            var audioClip = _clips.FirstOrDefault(c => c.Id == videoClip.LinkedAudioClipId);
            if (audioClip == null) continue;

            // 비디오 클립 중심점
            double videoX = TimeToX(videoClip.StartTimeMs) + DurationToWidth(videoClip.DurationMs) / 2;
            double videoY = GetTrackYPosition(videoClip.TrackIndex);
            var videoTrack = GetTrackByIndex(videoClip.TrackIndex);
            if (videoTrack == null) continue;
            double videoHeight = videoTrack.Height - 10;
            double videoCenterY = videoY + videoHeight / 2 + 5;

            // 오디오 클립 중심점
            double audioX = TimeToX(audioClip.StartTimeMs) + DurationToWidth(audioClip.DurationMs) / 2;
            double audioY = GetTrackYPosition(audioClip.TrackIndex);
            var audioTrack = GetTrackByIndex(audioClip.TrackIndex);
            if (audioTrack == null) continue;
            double audioHeight = audioTrack.Height - 10;
            double audioCenterY = audioY + audioHeight / 2 + 5;

            // 연결선 (점선, 반투명 시안색)
            var linkPen = new Pen(
                new SolidColorBrush(Color.FromArgb(120, 80, 220, 255)),
                1.5)
            {
                DashStyle = new DashStyle(new double[] { 3, 3 }, 0)
            };

            context.DrawLine(linkPen,
                new Point(videoX, videoCenterY),
                new Point(audioX, audioCenterY));

            // 연결 아이콘 (작은 원 - 비디오 클립 쪽)
            var videoIconRect = new Rect(videoX - 4, videoCenterY - 4, 8, 8);
            context.FillRectangle(
                new SolidColorBrush(Color.FromRgb(80, 220, 255)),
                videoIconRect);
            context.DrawRectangle(
                new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1),
                videoIconRect);

            // 연결 아이콘 (작은 원 - 오디오 클립 쪽)
            var audioIconRect = new Rect(audioX - 4, audioCenterY - 4, 8, 8);
            context.FillRectangle(
                new SolidColorBrush(Color.FromRgb(80, 220, 255)),
                audioIconRect);
            context.DrawRectangle(
                new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1),
                audioIconRect);
        }
    }

    /// <summary>
    /// 마우스 위치에서 키프레임 검색 (HitTest)
    /// </summary>
    private (Keyframe?, KeyframeSystem?, ClipModel?) GetKeyframeAtPosition(Point point)
    {
        if (_viewModel == null) return (null, null, null);

        // 선택된 클립에서만 키프레임 검색
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

                // 10px 임계값
                if (Math.Abs(point.X - keyframeX) < 10 && Math.Abs(point.Y - keyframeY) < 10)
                    return (keyframe, keyframeSystem, clip);
            }
        }

        return (null, null, null);
    }

    private void DrawPlayhead(DrawingContext context)
    {
        if (_viewModel == null) return;

        double x = TimeToX(_viewModel.CurrentTimeMs);

        // Playhead 그림자 (깊이감)
        var shadowPen = new Pen(
            new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            3);
        context.DrawLine(shadowPen,
            new Point(x + 1.5, 0),
            new Point(x + 1.5, Bounds.Height));

        // Playhead 본체 (밝은 빨간색)
        var playheadPen = new Pen(
            new SolidColorBrush(Color.FromRgb(255, 50, 50)),
            2.5);
        context.DrawLine(playheadPen,
            new Point(x, 0),
            new Point(x, Bounds.Height));

        // Playhead 헤드 (상단 삼각형 - DaVinci Resolve 스타일)
        var headGeometry = new StreamGeometry();
        using (var ctx = headGeometry.Open())
        {
            ctx.BeginFigure(new Point(x, 0), true);
            ctx.LineTo(new Point(x - 8, -12));
            ctx.LineTo(new Point(x + 8, -12));
            ctx.EndFigure(true);
        }

        // 헤드 그림자
        var headShadowGeometry = new StreamGeometry();
        using (var ctx = headShadowGeometry.Open())
        {
            ctx.BeginFigure(new Point(x + 1, 1), true);
            ctx.LineTo(new Point(x - 7, -11));
            ctx.LineTo(new Point(x + 9, -11));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            null,
            headShadowGeometry);

        // 헤드 본체 (그라디언트)
        var headGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromRgb(255, 80, 80), 0),
                new GradientStop(Color.FromRgb(255, 40, 40), 1)
            }
        };
        context.DrawGeometry(
            headGradient,
            new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1.2),
            headGeometry);
    }

    /// <summary>
    /// 클립 툴팁 렌더링 (호버 시 상세 정보 표시)
    /// </summary>
    private void DrawClipTooltip(DrawingContext context, ClipModel clip)
    {
        double x = TimeToX(clip.StartTimeMs);
        double width = DurationToWidth(clip.DurationMs);
        double y = GetTrackYPosition(clip.TrackIndex);
        var track = GetTrackByIndex(clip.TrackIndex);
        if (track == null) return;

        // 툴팁 내용 준비
        var fileName = System.IO.Path.GetFileName(clip.FilePath);
        var duration = TimeSpan.FromMilliseconds(clip.DurationMs);
        var durationStr = duration.ToString(@"mm\:ss\.fff");
        var startTime = TimeSpan.FromMilliseconds(clip.StartTimeMs);
        var startTimeStr = startTime.ToString(@"mm\:ss\.fff");

        var tooltipLines = new[]
        {
            $"📁 {fileName}",
            $"⏱ Duration: {durationStr}",
            $"▶ Start: {startTimeStr}",
            $"🎬 Track: {track.Name}"
        };

        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal);
        const double fontSize = 11;
        const double lineHeight = 16;
        const double padding = 8;

        // 텍스트 크기 계산
        double maxTextWidth = 0;
        foreach (var line in tooltipLines)
        {
            var text = new FormattedText(
                line,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White);
            maxTextWidth = Math.Max(maxTextWidth, text.Width);
        }

        // 툴팁 위치 (클립 위쪽, 화면 경계 체크)
        double tooltipX = x + width / 2 - maxTextWidth / 2 - padding;
        double tooltipY = y - (tooltipLines.Length * lineHeight) - padding * 2 - 10;

        // 화면 경계 체크
        tooltipX = Math.Clamp(tooltipX, 10, Bounds.Width - maxTextWidth - padding * 2 - 10);
        tooltipY = Math.Max(10, tooltipY);

        double tooltipWidth = maxTextWidth + padding * 2;
        double tooltipHeight = tooltipLines.Length * lineHeight + padding * 2;

        // 툴팁 배경 (프로페셔널 그라디언트 + 그림자)
        var shadowRect = new Rect(tooltipX + 3, tooltipY + 3, tooltipWidth, tooltipHeight);
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            shadowRect);

        var bgRect = new Rect(tooltipX, tooltipY, tooltipWidth, tooltipHeight);
        var bgGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(240, 50, 50, 52), 0),
                new GradientStop(Color.FromArgb(250, 40, 40, 42), 1)
            }
        };
        context.FillRectangle(bgGradient, bgRect);

        // 테두리 (시안색)
        var borderPen = new Pen(
            new SolidColorBrush(Color.FromArgb(200, 80, 220, 255)),
            1.5);
        context.DrawRectangle(borderPen, bgRect);

        // 텍스트 렌더링
        double textY = tooltipY + padding;
        foreach (var line in tooltipLines)
        {
            var text = new FormattedText(
                line,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White);

            context.DrawText(text, new Point(tooltipX + padding, textY));
            textY += lineHeight;
        }

        // 아래쪽 화살표 (툴팁이 클립을 가리키도록)
        var arrowGeometry = new StreamGeometry();
        using (var ctx = arrowGeometry.Open())
        {
            double arrowX = x + width / 2;
            double arrowY = tooltipY + tooltipHeight;

            ctx.BeginFigure(new Point(arrowX, arrowY + 8), true);
            ctx.LineTo(new Point(arrowX - 6, arrowY));
            ctx.LineTo(new Point(arrowX + 6, arrowY));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(250, 40, 40, 42)),
            new Pen(new SolidColorBrush(Color.FromArgb(200, 80, 220, 255)), 1.5),
            arrowGeometry);
    }

    private void DrawSnapGuideline(DrawingContext context, long timeMs)
    {
        double x = TimeToX(timeMs);

        // Snap 가이드라인 그림자
        var shadowPen = new Pen(
            new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            2)
        {
            DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
        };
        context.DrawLine(shadowPen,
            new Point(x + 1, 0),
            new Point(x + 1, Bounds.Height));

        // Snap 가이드라인 본체 (밝은 황금색)
        var pen = new Pen(
            new SolidColorBrush(Color.FromRgb(255, 220, 80)),
            1.5)
        {
            DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
        };
        context.DrawLine(pen,
            new Point(x, 0),
            new Point(x, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        // 중간 버튼: Pan 시작
        if (properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartPoint = point;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        // 왼쪽 버튼: Razor 모드 또는 클립 선택/드래그/트림
        if (properties.IsLeftButtonPressed)
        {
            // 1. 키프레임 HitTest (최우선)
            var (keyframe, keyframeSystem, clip) = GetKeyframeAtPosition(point);
            if (keyframe != null && keyframeSystem != null && clip != null)
            {
                _isDraggingKeyframe = true;
                _draggingKeyframe = keyframe;
                _draggingKeyframeSystem = keyframeSystem;
                _draggingKeyframeClip = clip;
                _dragStartPoint = point;
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
                return; // 다른 처리 스킵
            }

            // 2. Razor 모드: 클립 자르기
            if (_viewModel != null && _viewModel.RazorModeEnabled)
            {
                var clickedClip = GetClipAtPosition(point);
                if (clickedClip != null && _viewModel.RazorTool != null)
                {
                    var cutTime = XToTime(point.X);

                    // Shift + 클릭: 모든 트랙 동시 자르기
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        _viewModel.RazorTool.CutAllTracksAtTime(cutTime);
                    }
                    else
                    {
                        _viewModel.RazorTool.CutClipAtTime(clickedClip, cutTime);
                    }

                    InvalidateVisual();
                    e.Handled = true;
                }
                return;
            }

            // 일반 모드: 클립 선택/드래그/트림
            _selectedClip = GetClipAtPosition(point);

            if (_selectedClip != null)
            {
                // Ctrl + 클릭: 다중 선택
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _viewModel != null)
                {
                    if (_viewModel.SelectedClips.Contains(_selectedClip))
                    {
                        // 이미 선택됨 → 제거
                        _viewModel.SelectedClips.Remove(_selectedClip);
                    }
                    else
                    {
                        // 추가 선택
                        _viewModel.SelectedClips.Add(_selectedClip);
                    }

                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }

                // 단일 선택 (Ctrl 없음)
                if (_viewModel != null)
                {
                    _viewModel.SelectedClips.Clear();
                    _viewModel.SelectedClips.Add(_selectedClip);
                }

                // 트림 엣지 감지
                _trimEdge = HitTestEdge(_selectedClip, point);

                if (_trimEdge != ClipEdge.None)
                {
                    // 트림 모드
                    _isTrimming = true;
                    _draggingClip = _selectedClip;
                    _dragStartPoint = point;
                    _originalStartTimeMs = _selectedClip.StartTimeMs;
                    _originalDurationMs = _selectedClip.DurationMs;
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                }
                else
                {
                    // 드래그 모드
                    _isDragging = true;
                    _draggingClip = _selectedClip;
                    _dragStartPoint = point;
                }
            }
            else
            {
                // 빈 공간 클릭: 선택 해제
                if (_viewModel != null)
                {
                    _viewModel.SelectedClips.Clear();
                }
            }

            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);

        // Razor 모드: Cross 커서
        if (_viewModel != null && _viewModel.RazorModeEnabled && !_isDragging && !_isTrimming && !_isPanning)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
        }
        else if (!_isDragging && !_isTrimming && !_isPanning)
        {
            Cursor = Cursor.Default;
        }

        // Pan 처리 (중간 버튼)
        if (_isPanning)
        {
            var delta = point - _panStartPoint;

            // ScrollViewer를 통해 스크롤
            var timelineCanvas = this.GetVisualAncestors().OfType<TimelineCanvas>().FirstOrDefault();
            if (timelineCanvas != null)
            {
                // ScrollViewer는 TimelineCanvas에 있으므로, 부모를 통해 접근
                // 간단한 방법: ScrollViewer를 찾아서 Offset 변경
                var scrollViewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                if (scrollViewer != null)
                {
                    scrollViewer.Offset = new Vector(
                        Math.Max(0, scrollViewer.Offset.X - delta.X),
                        Math.Max(0, scrollViewer.Offset.Y - delta.Y)
                    );
                }
            }

            _panStartPoint = point;
            e.Handled = true;
            return;
        }

        // 키프레임 드래그 처리 (최우선)
        if (_isDraggingKeyframe && _draggingKeyframe != null && _draggingKeyframeSystem != null && _draggingKeyframeClip != null)
        {
            var deltaX = point.X - _dragStartPoint.X;
            var deltaTimeMs = (long)(deltaX / _pixelsPerMs);
            var newTime = Math.Max(0, _draggingKeyframe.Time + deltaTimeMs / 1000.0);

            // 클립 범위 내로 제한
            var clipDurationSec = _draggingKeyframeClip.DurationMs / 1000.0;
            newTime = Math.Clamp(newTime, 0, clipDurationSec);

            _draggingKeyframeSystem.UpdateKeyframe(_draggingKeyframe, newTime, _draggingKeyframe.Value);

            _dragStartPoint = point;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // 트림 처리
        if (_isTrimming && _draggingClip != null)
        {
            var currentTime = XToTime(point.X);

            if (_trimEdge == ClipEdge.Left)
            {
                // 왼쪽 트림: StartTimeMs 증가, DurationMs 감소
                var newStartTime = Math.Max(0, currentTime);
                var maxStartTime = _originalStartTimeMs + _originalDurationMs - 100; // 최소 100ms 유지

                newStartTime = Math.Min(newStartTime, maxStartTime);

                var deltaTime = newStartTime - _originalStartTimeMs;
                _draggingClip.StartTimeMs = newStartTime;
                _draggingClip.DurationMs = _originalDurationMs - deltaTime;

                // TrimStartMs도 조정 (Rust에서 처리할 예정)
                // _draggingClip.TrimStartMs += deltaTime;
            }
            else if (_trimEdge == ClipEdge.Right)
            {
                // 오른쪽 트림: DurationMs만 조정
                var newEndTime = Math.Max(_draggingClip.StartTimeMs + 100, currentTime); // 최소 100ms 유지
                _draggingClip.DurationMs = newEndTime - _draggingClip.StartTimeMs;
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // 클립 드래그 처리
        if (_isDragging && _draggingClip != null)
        {
            var deltaX = point.X - _dragStartPoint.X;

            // 드래그로 클립 이동
            var deltaTimeMs = (long)(deltaX / _pixelsPerMs);
            var newStartTime = Math.Max(0, _draggingClip.StartTimeMs + deltaTimeMs);

            // Snap 적용
            if (_snapService != null && _viewModel != null && _viewModel.SnapEnabled)
            {
                var snapResult = _snapService.GetSnapTarget(newStartTime, _draggingClip);
                _draggingClip.StartTimeMs = snapResult.TimeMs;
                _lastSnappedTimeMs = snapResult.Snapped ? snapResult.TimeMs : -1;
            }
            else
            {
                _draggingClip.StartTimeMs = newStartTime;
                _lastSnappedTimeMs = -1;
            }

            _dragStartPoint = point;
            InvalidateVisual();
        }

        // 호버 감지 (드래그/트림/팬 중이 아닐 때)
        if (!_isDragging && !_isTrimming && !_isPanning && !_isDraggingKeyframe)
        {
            var hoveredClip = GetClipAtPosition(point);
            if (hoveredClip != _hoveredClip)
            {
                _hoveredClip = hoveredClip;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Pan 종료
        if (_isPanning)
        {
            _isPanning = false;
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        // 키프레임 드래그 종료 (최우선)
        if (_isDraggingKeyframe)
        {
            _isDraggingKeyframe = false;
            _draggingKeyframe = null;
            _draggingKeyframeSystem = null;
            _draggingKeyframeClip = null;
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        // 트림 종료
        if (_isTrimming)
        {
            _isTrimming = false;
            _trimEdge = ClipEdge.None;
            _draggingClip = null;
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        // 클립 드래그 종료
        _isDragging = false;
        _draggingClip = null;
        _lastSnappedTimeMs = -1;
        InvalidateVisual(); // Snap 가이드라인 제거
        e.Handled = true;
    }

    /// <summary>
    /// Zoom/Pan 마우스 휠 처리
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl + 마우스휠: 수평 Zoom (0.01 ~ 1.0)
            var zoomFactor = e.Delta.Y > 0 ? 1.1 : 0.9;
            var newZoom = Math.Clamp(_pixelsPerMs * zoomFactor, 0.01, 1.0);

            // TimelineCanvas를 통해 전체 컴포넌트에 Zoom 적용
            var timelineCanvas = this.GetVisualAncestors().OfType<TimelineCanvas>().FirstOrDefault();
            if (timelineCanvas != null)
            {
                timelineCanvas.SetZoom(newZoom);
            }

            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Alt + 마우스휠: 수직 Zoom (트랙 높이 30~200px)
            var heightDelta = e.Delta.Y > 0 ? 10 : -10;

            // 마우스 위치의 트랙 찾기
            var mousePos = e.GetPosition(this);
            var trackIndex = GetTrackIndexAtY(mousePos.Y);
            var track = GetTrackByIndex(trackIndex);

            if (track != null)
            {
                track.Height = Math.Clamp(track.Height + heightDelta, 30, 200);
                InvalidateVisual();

                // TrackListPanel도 업데이트
                var timelineCanvas = this.GetVisualAncestors().OfType<TimelineCanvas>().FirstOrDefault();
                if (timelineCanvas != null && _viewModel != null)
                {
                    // ViewModel 변경 감지로 자동 업데이트
                }
            }

            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift + 마우스휠: 수평 스크롤
            // ScrollViewer가 자동으로 처리하므로 기본 동작 유지
            // e.Handled = false;
        }
        else
        {
            // 마우스휠 기본: 수직 스크롤
            // ScrollViewer가 자동으로 처리하므로 기본 동작 유지
            // e.Handled = false;
        }
    }

    private ClipModel? GetClipAtPosition(Point point)
    {
        foreach (var clip in _clips)
        {
            double x = TimeToX(clip.StartTimeMs);
            double width = DurationToWidth(clip.DurationMs);
            double y = GetTrackYPosition(clip.TrackIndex);
            var track = GetTrackByIndex(clip.TrackIndex);
            if (track == null) continue;

            double height = track.Height - 10;
            var clipRect = new Rect(x, y + 5, Math.Max(width, MinClipWidth), height);

            if (clipRect.Contains(point))
            {
                return clip;
            }
        }

        return null;
    }

    /// <summary>
    /// 클립 엣지 HitTest (트림 핸들)
    /// </summary>
    private ClipEdge HitTestEdge(ClipModel clip, Point point)
    {
        double x = TimeToX(clip.StartTimeMs);
        double width = DurationToWidth(clip.DurationMs);
        double y = GetTrackYPosition(clip.TrackIndex);
        var track = GetTrackByIndex(clip.TrackIndex);
        if (track == null) return ClipEdge.None;

        double height = track.Height - 10;
        var clipRect = new Rect(x, y + 5, Math.Max(width, MinClipWidth), height);

        if (!clipRect.Contains(point))
            return ClipEdge.None;

        const double EdgeThreshold = 10; // 10px 트림 핸들 영역

        // 왼쪽 엣지
        if (point.X < clipRect.Left + EdgeThreshold)
            return ClipEdge.Left;

        // 오른쪽 엣지
        if (point.X > clipRect.Right - EdgeThreshold)
            return ClipEdge.Right;

        return ClipEdge.None;
    }

    private void HandleDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains("MediaItem"))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void HandleDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains("MediaItem") && _viewModel != null)
        {
            var mediaItem = e.Data.Get("MediaItem") as MediaItem;
            if (mediaItem != null)
            {
                var dropPoint = e.GetPosition(this);

                // 드롭 위치를 타임라인 시간과 트랙으로 변환
                long startTimeMs = XToTime(dropPoint.X);
                int trackIndex = GetTrackIndexAtY(dropPoint.Y);

                // ViewModel을 통해 클립 추가
                Dispatcher.UIThread.Post(() =>
                {
                    _viewModel.AddClipFromMediaItem(mediaItem, startTimeMs, trackIndex);
                });
            }

            e.Handled = true;
        }
    }

    private double TimeToX(long timeMs)
    {
        return timeMs * _pixelsPerMs - _scrollOffsetX;
    }

    private double DurationToWidth(long durationMs)
    {
        return durationMs * _pixelsPerMs;
    }

    private long XToTime(double x)
    {
        return (long)((x + _scrollOffsetX) / _pixelsPerMs);
    }

    private double GetTrackYPosition(int trackIndex)
    {
        double y = 0;
        for (int i = 0; i < trackIndex && i < _videoTracks.Count; i++)
        {
            y += _videoTracks[i].Height;
        }
        return y;
    }

    private TrackModel? GetTrackByIndex(int index)
    {
        if (index < _videoTracks.Count)
            return _videoTracks[index];

        int audioIndex = index - _videoTracks.Count;
        if (audioIndex >= 0 && audioIndex < _audioTracks.Count)
            return _audioTracks[audioIndex];

        return null;
    }

    private int GetTrackIndexAtY(double y)
    {
        double currentY = 0;

        // 비디오 트랙 검사
        for (int i = 0; i < _videoTracks.Count; i++)
        {
            if (y >= currentY && y < currentY + _videoTracks[i].Height)
                return i;
            currentY += _videoTracks[i].Height;
        }

        // 오디오 트랙 검사
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            if (y >= currentY && y < currentY + _audioTracks[i].Height)
                return _videoTracks.Count + i;
            currentY += _audioTracks[i].Height;
        }

        return 0; // 기본값
    }
}

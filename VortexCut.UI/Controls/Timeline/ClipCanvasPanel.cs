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

        // Playhead
        DrawPlayhead(context);
    }

    private void DrawTrackBackgrounds(DrawingContext context)
    {
        var trackBrush = new SolidColorBrush(Color.Parse("#2D2D30"));
        var borderPen = new Pen(new SolidColorBrush(Color.Parse("#444444")), 1);

        // 비디오 트랙
        for (int i = 0; i < _videoTracks.Count; i++)
        {
            var track = _videoTracks[i];
            double y = i * track.Height;
            var trackRect = new Rect(0, y, Bounds.Width, track.Height);

            context.FillRectangle(trackBrush, trackRect);
            context.DrawRectangle(borderPen, trackRect);
        }

        // 오디오 트랙
        double audioStartY = _videoTracks.Sum(t => t.Height);
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            var track = _audioTracks[i];
            double y = audioStartY + i * track.Height;
            var trackRect = new Rect(0, y, Bounds.Width, track.Height);

            context.FillRectangle(trackBrush, trackRect);
            context.DrawRectangle(borderPen, trackRect);
        }
    }

    private void DrawClips(DrawingContext context)
    {
        foreach (var clip in _clips)
        {
            bool isSelected = _viewModel?.SelectedClips.Contains(clip) ?? false;
            DrawClip(context, clip, isSelected);
        }
    }

    private void DrawClip(DrawingContext context, ClipModel clip, bool isSelected)
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

        context.FillRectangle(gradientBrush, clipRect);

        // 오디오 웨이브폼 (간단한 시뮬레이션)
        if (isAudioClip && width > 50)
        {
            DrawAudioWaveform(context, clipRect);
        }

        // 테두리 (선택된 클립은 하얀색, 일반은 회색)
        var borderPen = isSelected
            ? new Pen(Brushes.White, 2)
            : new Pen(new SolidColorBrush(Color.Parse("#4A4A4C")), 1);

        context.DrawRectangle(borderPen, clipRect);

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
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
                12,
                Brushes.White);

            // 텍스트 배경 (반투명 검은색)
            var textBgRect = new Rect(x + 4, y + 8, text.Width + 6, text.Height + 4);
            context.FillRectangle(
                new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                textBgRect);

            // 텍스트
            context.DrawText(text, new Point(x + 7, y + 10));

            // 클립 지속시간 표시 (우측 상단)
            if (width > 100)
            {
                var duration = TimeSpan.FromMilliseconds(clip.DurationMs);
                var durationText = duration.ToString(@"mm\:ss");
                var durationFormatted = new FormattedText(
                    durationText,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal),
                    10,
                    new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));

                var durationX = x + width - durationFormatted.Width - 7;
                var durationBgRect = new Rect(durationX - 3, y + 8, durationFormatted.Width + 6, durationFormatted.Height + 4);
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    durationBgRect);
                context.DrawText(durationFormatted, new Point(durationX, y + 10));
            }
        }

        // 키프레임 렌더링 (선택된 클립만)
        if (isSelected && _viewModel != null)
        {
            DrawKeyframes(context, clip);
        }
    }

    /// <summary>
    /// 오디오 웨이브폼 렌더링 (간단한 시뮬레이션)
    /// </summary>
    private void DrawAudioWaveform(DrawingContext context, Rect clipRect)
    {
        const int SampleInterval = 4; // 4픽셀마다 샘플
        const double MaxAmplitude = 0.4; // 클립 높이의 40%

        var waveformBrush = new SolidColorBrush(Color.FromArgb(180, 100, 200, 100)); // 반투명 밝은 초록
        var centerY = clipRect.Top + clipRect.Height / 2;

        // 간단한 사인파 + 랜덤 노이즈로 웨이브폼 시뮬레이션
        var random = new System.Random((int)clipRect.X); // 일관된 랜덤 시드
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            bool firstPoint = true;
            for (double x = clipRect.Left; x < clipRect.Right; x += SampleInterval)
            {
                // 사인파 + 랜덤 노이즈
                double phase = (x - clipRect.Left) / 20.0;
                double sine = Math.Sin(phase) * 0.5;
                double noise = (random.NextDouble() - 0.5) * 0.5;
                double amplitude = (sine + noise) * MaxAmplitude * clipRect.Height;

                var topPoint = new Point(x, centerY - Math.Abs(amplitude));
                var bottomPoint = new Point(x, centerY + Math.Abs(amplitude));

                if (firstPoint)
                {
                    ctx.BeginFigure(topPoint, true);
                    firstPoint = false;
                }
                else
                {
                    ctx.LineTo(topPoint);
                }
            }

            // 하단 경로 (거울 대칭)
            for (double x = clipRect.Right - SampleInterval; x >= clipRect.Left; x -= SampleInterval)
            {
                double phase = (x - clipRect.Left) / 20.0;
                double sine = Math.Sin(phase) * 0.5;
                double noise = (random.NextDouble() - 0.5) * 0.5;
                double amplitude = (sine + noise) * MaxAmplitude * clipRect.Height;

                var bottomPoint = new Point(x, centerY + Math.Abs(amplitude));
                ctx.LineTo(bottomPoint);
            }

            ctx.EndFigure(true);
        }

        context.DrawGeometry(waveformBrush, null, geometry);
    }

    private void DrawKeyframes(DrawingContext context, ClipModel clip)
    {
        var keyframeSystem = GetKeyframeSystem(clip, _viewModel.SelectedKeyframeSystem);
        if (keyframeSystem == null || keyframeSystem.Keyframes.Count == 0) return;

        double clipX = TimeToX(clip.StartTimeMs);
        double clipY = GetTrackYPosition(clip.TrackIndex);
        double keyframeY = clipY + 20; // 클립 상단에서 20px

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
        const double Size = 8;

        // 보간 타입에 따라 색상 변경
        var brush = interpolation switch
        {
            InterpolationType.Linear => Brushes.Yellow,
            InterpolationType.Bezier => Brushes.Cyan,
            InterpolationType.EaseIn => Brushes.LightGreen,
            InterpolationType.EaseOut => Brushes.LightBlue,
            InterpolationType.EaseInOut => Brushes.Orange,
            InterpolationType.Hold => Brushes.Red,
            _ => Brushes.Yellow
        };

        // 다이아몬드 그리기
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, y - Size / 2), true); // 상단
            ctx.LineTo(new Point(x + Size / 2, y)); // 오른쪽
            ctx.LineTo(new Point(x, y + Size / 2)); // 하단
            ctx.LineTo(new Point(x - Size / 2, y)); // 왼쪽
            ctx.EndFigure(true);
        }

        context.DrawGeometry(brush, new Pen(Brushes.Black, 1), geometry);
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
        var pen = new Pen(Brushes.Red, 2);
        context.DrawLine(pen,
            new Point(x, 0),
            new Point(x, Bounds.Height));
    }

    private void DrawSnapGuideline(DrawingContext context, long timeMs)
    {
        double x = TimeToX(timeMs);
        var pen = new Pen(Brushes.Yellow, 1)
        {
            DashStyle = DashStyle.Dash
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

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// After Effects 스타일 그래프 에디터 (Value Graph / Speed Graph)
/// </summary>
public class GraphEditor : Control
{
    private TimelineViewModel? _viewModel;
    private ClipModel? _selectedClip;
    private KeyframeSystem? _keyframeSystem;

    // 뷰포트 변환
    private double _zoomX = 100; // 픽셀/초
    private double _zoomY = 100; // 픽셀/단위
    private double _panX = 0;
    private double _panY = 0;

    // 드래그 상태
    private Keyframe? _selectedKeyframe;
    private BezierHandle? _selectedHandle;
    private Keyframe? _selectedHandleKeyframe;
    private bool _isDraggingKeyframe;
    private bool _isDraggingHandle;
    private bool _isPanning;
    private Point _dragStartPoint;

    // 그리드 설정
    private const double GridSpacing = 50;

    public GraphEditor()
    {
        ClipToBounds = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainViewModel mainViewModel)
        {
            _viewModel = mainViewModel.Timeline;
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TimelineViewModel.SelectedClips) ||
                    e.PropertyName == nameof(TimelineViewModel.SelectedKeyframeSystem))
                {
                    UpdateSelectedClipAndKeyframeSystem();
                    InvalidateVisual();
                }
            };
            UpdateSelectedClipAndKeyframeSystem();
        }
    }

    private void UpdateSelectedClipAndKeyframeSystem()
    {
        if (_viewModel == null) return;

        _selectedClip = _viewModel.SelectedClips.FirstOrDefault();
        if (_selectedClip != null)
        {
            _keyframeSystem = GetKeyframeSystem(_selectedClip, _viewModel.SelectedKeyframeSystem);
        }
        else
        {
            _keyframeSystem = null;
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

    // 좌표 변환
    private Point ModelToScreen(double time, double value)
    {
        double x = (time * _zoomX) - _panX + Bounds.Width / 2;
        double y = Bounds.Height / 2 - (value * _zoomY) + _panY;
        return new Point(x, y);
    }

    private (double time, double value) ScreenToModel(Point screen)
    {
        double time = (screen.X - Bounds.Width / 2 + _panX) / _zoomX;
        double value = (Bounds.Height / 2 - screen.Y + _panY) / _zoomY;
        return (time, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 1. 프로페셔널 그라디언트 배경 (After Effects 스타일)
        var backgroundGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#252527"), 0),
                new GradientStop(Color.Parse("#1E1E20"), 0.5),
                new GradientStop(Color.Parse("#181A18"), 1)
            }
        };
        context.FillRectangle(backgroundGradient, Bounds);

        // 2. 미묘한 비네팅 효과 (가장자리 어둡게)
        var vignetteBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = RelativeScalar.Parse("80%"),
            RadiusY = RelativeScalar.Parse("80%"),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.3),
                new GradientStop(Color.FromArgb(40, 0, 0, 0), 1)
            }
        };
        context.FillRectangle(vignetteBrush, Bounds);

        // 3. 그리드
        DrawGrid(context);

        // 4. 축 (X축, Y축)
        DrawAxes(context);

        // 5. 키프레임 곡선 (샘플링)
        if (_keyframeSystem != null && _keyframeSystem.Keyframes.Count > 0)
        {
            DrawKeyframeCurve(context);
        }

        // 6. 키프레임 포인트
        if (_keyframeSystem != null && _keyframeSystem.Keyframes.Count > 0)
        {
            DrawKeyframePoints(context);
        }

        // 7. 베지어 핸들
        if (_selectedKeyframe != null)
        {
            DrawBezierHandles(context, _selectedKeyframe);
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        // 프로페셔널 다층 그리드 (After Effects 스타일)
        var microGridPen = new Pen(new SolidColorBrush(Color.FromArgb(20, 80, 80, 80)), 0.5);
        var minorGridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 100, 100, 100)), 0.8);
        var majorGridPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 120, 120, 120)), 1.2);

        double microSpacing = GridSpacing / 4;
        double minorSpacing = GridSpacing;
        double majorSpacing = GridSpacing * 5;

        // 마이크로 그리드 (가장 촘촘, 가장 연함)
        for (double y = 0; y < Bounds.Height; y += microSpacing)
        {
            if (y % minorSpacing > 0.1)
                context.DrawLine(microGridPen, new Point(0, y), new Point(Bounds.Width, y));
        }
        for (double x = 0; x < Bounds.Width; x += microSpacing)
        {
            if (x % minorSpacing > 0.1)
                context.DrawLine(microGridPen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        // 마이너 그리드 (중간)
        for (double y = 0; y < Bounds.Height; y += minorSpacing)
        {
            if (y % majorSpacing > 0.1)
                context.DrawLine(minorGridPen, new Point(0, y), new Point(Bounds.Width, y));
        }
        for (double x = 0; x < Bounds.Width; x += minorSpacing)
        {
            if (x % majorSpacing > 0.1)
                context.DrawLine(minorGridPen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        // 메이저 그리드 (가장 진함)
        for (double y = 0; y < Bounds.Height; y += majorSpacing)
        {
            context.DrawLine(majorGridPen, new Point(0, y), new Point(Bounds.Width, y));
        }
        for (double x = 0; x < Bounds.Width; x += majorSpacing)
        {
            context.DrawLine(majorGridPen, new Point(x, 0), new Point(x, Bounds.Height));
        }
    }

    private void DrawAxes(DrawingContext context)
    {
        // X축 (시간) - 프로페셔널 스타일
        double y0 = ModelToScreen(0, 0).Y;
        if (y0 >= 0 && y0 <= Bounds.Height)
        {
            // 축 그림자
            var xAxisShadowPen = new Pen(
                new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                3);
            context.DrawLine(xAxisShadowPen,
                new Point(0, y0 + 1),
                new Point(Bounds.Width, y0 + 1));

            // 축 본체 (밝은 회색)
            var xAxisPen = new Pen(
                new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                2.5);
            context.DrawLine(xAxisPen,
                new Point(0, y0),
                new Point(Bounds.Width, y0));

            // 축 하이라이트 (상단)
            var xAxisHighlightPen = new Pen(
                new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                1);
            context.DrawLine(xAxisHighlightPen,
                new Point(0, y0 - 1),
                new Point(Bounds.Width, y0 - 1));
        }

        // Y축 (값) - 프로페셔널 스타일
        double x0 = ModelToScreen(0, 0).X;
        if (x0 >= 0 && x0 <= Bounds.Width)
        {
            // 축 그림자
            var yAxisShadowPen = new Pen(
                new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                3);
            context.DrawLine(yAxisShadowPen,
                new Point(x0 + 1, 0),
                new Point(x0 + 1, Bounds.Height));

            // 축 본체 (밝은 회색)
            var yAxisPen = new Pen(
                new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                2.5);
            context.DrawLine(yAxisPen,
                new Point(x0, 0),
                new Point(x0, Bounds.Height));

            // 축 하이라이트 (왼쪽)
            var yAxisHighlightPen = new Pen(
                new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                1);
            context.DrawLine(yAxisHighlightPen,
                new Point(x0 - 1, 0),
                new Point(x0 - 1, Bounds.Height));
        }
    }

    private void DrawKeyframeCurve(DrawingContext context)
    {
        if (_keyframeSystem == null || _keyframeSystem.Keyframes.Count < 2) return;

        double startTime = _keyframeSystem.Keyframes.First().Time;
        double endTime = _keyframeSystem.Keyframes.Last().Time;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool firstPoint = true;

            // 0.01초 간격 샘플링
            for (double t = startTime; t <= endTime; t += 0.01)
            {
                double value = _keyframeSystem.Interpolate(t);
                var screenPoint = ModelToScreen(t, value);

                // 화면 밖은 스킵
                if (screenPoint.X < -100 || screenPoint.X > Bounds.Width + 100) continue;

                if (firstPoint)
                {
                    ctx.BeginFigure(screenPoint, false);
                    firstPoint = false;
                }
                else
                {
                    ctx.LineTo(screenPoint);
                }
            }
        }

        // 곡선 그림자 (깊이감)
        var shadowPen = new Pen(
            new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            3.5);

        var shadowGeometry = new StreamGeometry();
        using (var ctx = shadowGeometry.Open())
        {
            bool firstPoint = true;
            for (double t = startTime; t <= endTime; t += 0.01)
            {
                double value = _keyframeSystem.Interpolate(t);
                var screenPoint = ModelToScreen(t, value);
                screenPoint = new Point(screenPoint.X + 1.5, screenPoint.Y + 1.5);

                if (screenPoint.X < -100 || screenPoint.X > Bounds.Width + 100) continue;

                if (firstPoint)
                {
                    ctx.BeginFigure(screenPoint, false);
                    firstPoint = false;
                }
                else
                {
                    ctx.LineTo(screenPoint);
                }
            }
        }
        context.DrawGeometry(null, shadowPen, shadowGeometry);

        // 곡선 본체 (밝은 시안 + 글로우)
        var glowPen = new Pen(
            new SolidColorBrush(Color.FromArgb(60, 80, 220, 255)),
            5);
        context.DrawGeometry(null, glowPen, geometry);

        var curvePen = new Pen(
            new SolidColorBrush(Color.FromRgb(80, 220, 255)),
            2.5);
        context.DrawGeometry(null, curvePen, geometry);
    }

    private void DrawKeyframePoints(DrawingContext context)
    {
        if (_keyframeSystem == null) return;

        foreach (var keyframe in _keyframeSystem.Keyframes)
        {
            var centerPoint = ModelToScreen(keyframe.Time, keyframe.Value);

            // 화면 밖은 스킵
            if (centerPoint.X < -50 || centerPoint.X > Bounds.Width + 50) continue;

            bool isSelected = keyframe == _selectedKeyframe;
            var radius = isSelected ? 7.0 : 5.0;

            // 키프레임 그림자
            var shadowRadius = radius + 1.5;
            var shadowPoint = new Point(centerPoint.X + 1.5, centerPoint.Y + 1.5);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                null,
                shadowPoint,
                shadowRadius,
                shadowRadius);

            // 키프레임 글로우 (선택된 경우)
            if (isSelected)
            {
                var glowRadius = radius + 4;
                context.DrawEllipse(
                    new SolidColorBrush(Color.FromArgb(80, 255, 220, 80)),
                    null,
                    centerPoint,
                    glowRadius,
                    glowRadius);
            }

            // 키프레임 본체 (그라디언트)
            Color innerColor = isSelected ? Color.FromRgb(255, 240, 100) : Color.FromRgb(255, 255, 255);
            Color outerColor = isSelected ? Color.FromRgb(255, 200, 60) : Color.FromRgb(200, 200, 200);

            var gradientBrush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(innerColor, 0),
                    new GradientStop(outerColor, 1)
                }
            };

            context.DrawEllipse(
                gradientBrush,
                new Pen(new SolidColorBrush(isSelected ? Color.FromRgb(255, 180, 40) : Color.FromRgb(100, 100, 100)), 2),
                centerPoint,
                radius,
                radius);
        }
    }

    private void DrawBezierHandles(DrawingContext context, Keyframe keyframe)
    {
        var centerPoint = ModelToScreen(keyframe.Time, keyframe.Value);

        // OutHandle (초록색)
        if (keyframe.OutHandle != null)
        {
            var handleTime = keyframe.Time + keyframe.OutHandle.TimeOffset;
            var handleValue = keyframe.Value + keyframe.OutHandle.ValueOffset;
            var handlePoint = ModelToScreen(handleTime, handleValue);

            // 핸들 라인 그림자
            var lineShadowPen = new Pen(
                new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                2.5);
            context.DrawLine(lineShadowPen,
                new Point(centerPoint.X + 1, centerPoint.Y + 1),
                new Point(handlePoint.X + 1, handlePoint.Y + 1));

            // 핸들 라인 본체 (점선)
            var linePen = new Pen(
                new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                2)
            {
                DashStyle = new DashStyle(new double[] { 3, 3 }, 0)
            };
            context.DrawLine(linePen, centerPoint, handlePoint);

            // 핸들 포인트 그림자
            var handleShadowPoint = new Point(handlePoint.X + 1.5, handlePoint.Y + 1.5);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                null,
                handleShadowPoint,
                6.5,
                6.5);

            // 핸들 포인트 본체 (그라디언트 초록)
            var handleGradient = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromRgb(150, 255, 150), 0),
                    new GradientStop(Color.FromRgb(80, 200, 80), 1)
                }
            };
            context.DrawEllipse(
                handleGradient,
                new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2),
                handlePoint,
                6,
                6);
        }

        // InHandle (빨간색)
        if (keyframe.InHandle != null)
        {
            var handleTime = keyframe.Time + keyframe.InHandle.TimeOffset;
            var handleValue = keyframe.Value + keyframe.InHandle.ValueOffset;
            var handlePoint = ModelToScreen(handleTime, handleValue);

            // 핸들 라인 그림자
            var lineShadowPen = new Pen(
                new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                2.5);
            context.DrawLine(lineShadowPen,
                new Point(centerPoint.X + 1, centerPoint.Y + 1),
                new Point(handlePoint.X + 1, handlePoint.Y + 1));

            // 핸들 라인 본체 (점선)
            var linePen = new Pen(
                new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                2)
            {
                DashStyle = new DashStyle(new double[] { 3, 3 }, 0)
            };
            context.DrawLine(linePen, centerPoint, handlePoint);

            // 핸들 포인트 그림자
            var handleShadowPoint = new Point(handlePoint.X + 1.5, handlePoint.Y + 1.5);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                null,
                handleShadowPoint,
                6.5,
                6.5);

            // 핸들 포인트 본체 (그라디언트 빨강)
            var handleGradient = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromRgb(255, 150, 150), 0),
                    new GradientStop(Color.FromRgb(200, 80, 80), 1)
                }
            };
            context.DrawEllipse(
                handleGradient,
                new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2),
                handlePoint,
                6,
                6);
        }
    }

    // 마우스 이벤트
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _dragStartPoint = point;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            // 1. 베지어 핸들 우선
            var (handle, handleKeyframe) = GetHandleAtPosition(point, threshold: 10);
            if (handle != null && handleKeyframe != null)
            {
                _isDraggingHandle = true;
                _selectedHandle = handle;
                _selectedHandleKeyframe = handleKeyframe;
                _selectedKeyframe = handleKeyframe;
                _dragStartPoint = point;
                Cursor = new Cursor(StandardCursorType.Hand);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // 2. 키프레임 선택
            var clickedKeyframe = GetKeyframeAtPosition(point, threshold: 10);
            if (clickedKeyframe != null)
            {
                _isDraggingKeyframe = true;
                _selectedKeyframe = clickedKeyframe;
                _dragStartPoint = point;
                Cursor = new Cursor(StandardCursorType.Hand);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // 3. 빈 공간 클릭 - 선택 해제
            _selectedKeyframe = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_isPanning)
        {
            var delta = point - _dragStartPoint;
            _panX -= delta.X;
            _panY -= delta.Y;
            _dragStartPoint = point;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isDraggingHandle && _selectedHandle != null && _selectedHandleKeyframe != null)
        {
            var (newTime, newValue) = ScreenToModel(point);
            _selectedHandle.TimeOffset = newTime - _selectedHandleKeyframe.Time;
            _selectedHandle.ValueOffset = newValue - _selectedHandleKeyframe.Value;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isDraggingKeyframe && _selectedKeyframe != null && _keyframeSystem != null)
        {
            var (newTime, newValue) = ScreenToModel(point);
            newTime = Math.Max(0, newTime);

            // 값 범위 제한 (0~100)
            newValue = Math.Clamp(newValue, 0, 100);

            _keyframeSystem.UpdateKeyframe(_selectedKeyframe, newTime, newValue);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPanning)
        {
            _isPanning = false;
            Cursor = Cursor.Default;
            e.Handled = true;
        }

        if (_isDraggingHandle)
        {
            _isDraggingHandle = false;
            _selectedHandle = null;
            _selectedHandleKeyframe = null;
            Cursor = Cursor.Default;
            e.Handled = true;
        }

        if (_isDraggingKeyframe)
        {
            _isDraggingKeyframe = false;
            Cursor = Cursor.Default;
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var mousePos = e.GetPosition(this);
            var (mouseTime, mouseValue) = ScreenToModel(mousePos);

            var zoomFactor = e.Delta.Y > 0 ? 1.1 : 0.9;
            _zoomX *= zoomFactor;
            _zoomY *= zoomFactor;

            // Zoom 후 마우스 위치 유지
            var newMouseScreen = ModelToScreen(mouseTime, mouseValue);
            _panX += (newMouseScreen.X - mousePos.X);
            _panY += (newMouseScreen.Y - mousePos.Y);

            InvalidateVisual();
            e.Handled = true;
        }
    }

    private Keyframe? GetKeyframeAtPosition(Point point, double threshold = 10)
    {
        if (_keyframeSystem == null) return null;

        foreach (var keyframe in _keyframeSystem.Keyframes)
        {
            var centerPoint = ModelToScreen(keyframe.Time, keyframe.Value);
            if (Distance(point, centerPoint) < threshold)
                return keyframe;
        }
        return null;
    }

    private (BezierHandle?, Keyframe?) GetHandleAtPosition(Point point, double threshold = 10)
    {
        if (_keyframeSystem == null) return (null, null);

        foreach (var keyframe in _keyframeSystem.Keyframes)
        {
            // OutHandle
            if (keyframe.OutHandle != null)
            {
                var handleTime = keyframe.Time + keyframe.OutHandle.TimeOffset;
                var handleValue = keyframe.Value + keyframe.OutHandle.ValueOffset;
                var handlePoint = ModelToScreen(handleTime, handleValue);

                if (Distance(point, handlePoint) < threshold)
                    return (keyframe.OutHandle, keyframe);
            }

            // InHandle
            if (keyframe.InHandle != null)
            {
                var handleTime = keyframe.Time + keyframe.InHandle.TimeOffset;
                var handleValue = keyframe.Value + keyframe.InHandle.ValueOffset;
                var handlePoint = ModelToScreen(handleTime, handleValue);

                if (Distance(point, handlePoint) < threshold)
                    return (keyframe.InHandle, keyframe);
            }
        }
        return (null, null);
    }

    private double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

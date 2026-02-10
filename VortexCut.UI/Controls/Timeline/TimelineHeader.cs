using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 타임라인 상단 헤더 (시간 눈금, 마커)
/// </summary>
public class TimelineHeader : Control
{
    private const double HeaderHeight = 80;
    private TimelineViewModel? _viewModel;
    private double _pixelsPerMs = 0.1;
    private double _scrollOffsetX = 0;
    private MarkerModel? _hoveredMarker;

    public void SetViewModel(TimelineViewModel viewModel)
    {
        _viewModel = viewModel;

        // 마커 추가/삭제 시 자동 새로고침
        _viewModel.Markers.CollectionChanged += (s, e) => InvalidateVisual();
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

        // 프로페셔널 그라디언트 배경 (DaVinci Resolve 스타일)
        var headerRect = new Rect(0, 0, Bounds.Width, HeaderHeight);
        var headerGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#3A3A3C"), 0),
                new GradientStop(Color.Parse("#2D2D30"), 0.5),
                new GradientStop(Color.Parse("#252527"), 1)
            }
        };
        context.FillRectangle(headerGradient, headerRect);

        // 하단 하이라이트 라인 (미묘한 3D 효과)
        var highlightPen = new Pen(
            new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            1);
        context.DrawLine(highlightPen,
            new Point(0, HeaderHeight - 1),
            new Point(Bounds.Width, HeaderHeight - 1));

        // 시간 눈금
        DrawTimeRuler(context);

        // 마커 (Phase 2D)
        if (_viewModel != null)
        {
            DrawMarkers(context);
        }

        // 상태 정보 표시 (우측 상단)
        DrawStatusInfo(context);

        // Zoom 바 시각화 (좌측 하단)
        DrawZoomBar(context);
    }

    private void DrawStatusInfo(DrawingContext context)
    {
        if (_viewModel == null) return;

        // 줌 레벨 표시
        var zoomPercent = (int)(_pixelsPerMs * 100);
        var zoomText = $"Zoom: {zoomPercent}%";

        // 현재 시간 (프레임 번호)
        var currentFrame = (int)(_viewModel.CurrentTimeMs * 30 / 1000); // 30fps 가정
        var frameText = $"Frame: {currentFrame}";

        // 클립 개수
        var clipCountText = $"Clips: {_viewModel.Clips.Count}";

        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
        var fontSize = 10.0;

        // 배경 박스 (프로페셔널 스타일)
        var infoText = $"{zoomText}  |  {frameText}  |  {clipCountText}";
        var text = new FormattedText(
            infoText,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White);

        var textX = Bounds.Width - text.Width - 15;
        var textY = 8;

        // 배경 (그라디언트 + 테두리)
        var bgRect = new Rect(textX - 8, textY - 4, text.Width + 16, text.Height + 8);
        var bgGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(200, 45, 45, 48), 0),
                new GradientStop(Color.FromArgb(220, 35, 35, 38), 1)
            }
        };
        context.FillRectangle(bgGradient, bgRect);

        // 테두리 (파란색 하이라이트)
        var borderPen = new Pen(
            new SolidColorBrush(Color.FromArgb(150, 0, 122, 204)),
            1);
        context.DrawRectangle(borderPen, bgRect);

        // 텍스트
        context.DrawText(text, new Point(textX, textY));
    }

    /// <summary>
    /// Zoom 바 시각화 (좌측 하단)
    /// </summary>
    private void DrawZoomBar(DrawingContext context)
    {
        const double barWidth = 150;
        const double barHeight = 8;
        const double barX = 15;
        const double barY = HeaderHeight - 20;

        // Zoom 범위: 0.01 ~ 1.0
        // 로그 스케일로 표시 (0.01이 왼쪽, 1.0이 오른쪽)
        double minZoom = 0.01;
        double maxZoom = 1.0;
        double normalizedZoom = (Math.Log10(_pixelsPerMs) - Math.Log10(minZoom)) /
                                (Math.Log10(maxZoom) - Math.Log10(minZoom));
        normalizedZoom = Math.Clamp(normalizedZoom, 0, 1);

        // 배경 트랙 (그라디언트)
        var trackRect = new Rect(barX, barY, barWidth, barHeight);
        var trackGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(180, 40, 40, 42), 0),
                new GradientStop(Color.FromArgb(200, 50, 50, 52), 1)
            }
        };
        context.FillRectangle(trackGradient, trackRect);

        // 테두리
        var trackBorderPen = new Pen(
            new SolidColorBrush(Color.FromArgb(150, 80, 80, 82)),
            1);
        context.DrawRectangle(trackBorderPen, trackRect);

        // 채워진 부분 (Zoom 레벨)
        var fillWidth = barWidth * normalizedZoom;
        var fillRect = new Rect(barX, barY, fillWidth, barHeight);
        var fillGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromRgb(0, 122, 204), 0),
                new GradientStop(Color.FromRgb(28, 151, 234), 0.5),
                new GradientStop(Color.FromRgb(80, 220, 255), 1)
            }
        };
        context.FillRectangle(fillGradient, fillRect);

        // 글로우 효과
        var glowRect = new Rect(barX, barY - 1, fillWidth, barHeight + 2);
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(40, 80, 220, 255)),
            glowRect);

        // 썸 (현재 위치 표시)
        var thumbX = barX + fillWidth;
        var thumbRect = new Rect(thumbX - 3, barY - 2, 6, barHeight + 4);
        var thumbGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromRgb(255, 255, 255), 0),
                new GradientStop(Color.FromRgb(200, 200, 200), 1)
            }
        };
        context.FillRectangle(thumbGradient, thumbRect);

        // 썸 테두리
        var thumbBorderPen = new Pen(
            new SolidColorBrush(Color.FromRgb(80, 220, 255)),
            1.5);
        context.DrawRectangle(thumbBorderPen, thumbRect);

        // 라벨 (ZOOM)
        var labelTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
        var label = new FormattedText(
            "ZOOM",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            labelTypeface,
            9,
            new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)));

        context.DrawText(label, new Point(barX, barY - 12));

        // 범위 표시 (최소/최대)
        var minLabel = new FormattedText(
            "1%",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal),
            8,
            new SolidColorBrush(Color.FromArgb(150, 150, 150, 150)));

        var maxLabel = new FormattedText(
            "100%",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal),
            8,
            new SolidColorBrush(Color.FromArgb(150, 150, 150, 150)));

        context.DrawText(minLabel, new Point(barX, barY + barHeight + 2));
        context.DrawText(maxLabel, new Point(barX + barWidth - maxLabel.Width, barY + barHeight + 2));
    }

    private void DrawTimeRuler(DrawingContext context)
    {
        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
        var fontSize = 10;

        // 작은 눈금 (100ms 간격)
        var smallTickPen = new Pen(new SolidColorBrush(Color.Parse("#555555")), 1);
        for (int i = 0; i < 1000; i++)
        {
            long timeMs = i * 100; // 100ms 간격
            double x = TimeToX(timeMs);

            if (x < 0 || x > Bounds.Width)
                continue;

            // 작은 눈금 (5px)
            if (i % 10 != 0) // 1초 단위는 큰 눈금으로
            {
                context.DrawLine(smallTickPen,
                    new Point(x, HeaderHeight - 5),
                    new Point(x, HeaderHeight));
            }
        }

        // 큰 눈금 및 텍스트 (1초 간격)
        var bigTickPen = new Pen(new SolidColorBrush(Color.Parse("#AAAAAA")), 1.5);
        for (int i = 0; i < 100; i++)
        {
            long timeMs = i * 1000; // 1초 간격
            double x = TimeToX(timeMs);

            if (x < 0 || x > Bounds.Width)
                continue;

            // 큰 눈금 (12px)
            context.DrawLine(bigTickPen,
                new Point(x, HeaderHeight - 12),
                new Point(x, HeaderHeight));

            // 시간 텍스트 (SemiBold, 배경 추가)
            var text = new FormattedText(
                $"{i}s",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White);

            // 텍스트 배경 (반투명)
            var textBgRect = new Rect(x + 1, HeaderHeight - 26, text.Width + 4, text.Height + 2);
            context.FillRectangle(
                new SolidColorBrush(Color.FromArgb(150, 45, 45, 48)),
                textBgRect);

            context.DrawText(text, new Point(x + 3, HeaderHeight - 25));
        }
    }

    private void DrawMarkers(DrawingContext context)
    {
        if (_viewModel == null) return;

        foreach (var marker in _viewModel.Markers)
        {
            double x = TimeToX(marker.TimeMs);

            // 화면 밖이면 스킵 (성능 최적화)
            if (x < -20 || x > Bounds.Width + 20)
                continue;

            var color = ArgbToColor(marker.ColorArgb);
            var brush = new SolidColorBrush(color);
            var size = marker.Type == MarkerType.Chapter ? 14.0 : 10.0;
            var yTop = 18.0;

            // 마커 그림자 (깊이감)
            var shadowGeometry = new StreamGeometry();
            using (var ctx = shadowGeometry.Open())
            {
                ctx.BeginFigure(new Point(x + 1, yTop + 1), true);
                ctx.LineTo(new Point(x - size / 2 + 1, yTop + size + 1));
                ctx.LineTo(new Point(x + size / 2 + 1, yTop + size + 1));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                null,
                shadowGeometry);

            // 마커 본체 (더 밝고 선명하게)
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x, yTop), true);
                ctx.LineTo(new Point(x - size / 2, yTop + size));
                ctx.LineTo(new Point(x + size / 2, yTop + size));
                ctx.EndFigure(true);
            }

            // 마커 내부 그라디언트
            var markerGradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(color, 0),
                    new GradientStop(Color.FromRgb(
                        (byte)Math.Max(0, color.R - 40),
                        (byte)Math.Max(0, color.G - 40),
                        (byte)Math.Max(0, color.B - 40)), 1)
                }
            };

            context.DrawGeometry(markerGradient, new Pen(Brushes.White, 0.8), geometry);

            // Region 마커: 프로페셔널 스타일 범위 표시
            if (marker.IsRegion)
            {
                double endX = TimeToX(marker.EndTimeMs);

                // 범위 배경 (반투명)
                var regionRect = new Rect(x, yTop + size, endX - x, HeaderHeight - yTop - size);
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
                    regionRect);

                // 상단 연결선 (더 두껍고 그라디언트)
                var linePen = new Pen(
                    new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops = new GradientStops
                        {
                            new GradientStop(color, 0),
                            new GradientStop(Color.FromArgb(180, color.R, color.G, color.B), 0.5),
                            new GradientStop(color, 1)
                        }
                    },
                    2.5);
                context.DrawLine(linePen, new Point(x, 30), new Point(endX, 30));

                // 끝 삼각형 (그림자 포함)
                var endShadowGeometry = new StreamGeometry();
                using (var ctx = endShadowGeometry.Open())
                {
                    ctx.BeginFigure(new Point(endX + 1, yTop + 1), true);
                    ctx.LineTo(new Point(endX - size / 2 + 1, yTop + size + 1));
                    ctx.LineTo(new Point(endX + size / 2 + 1, yTop + size + 1));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                    null,
                    endShadowGeometry);

                var endGeometry = new StreamGeometry();
                using (var ctx = endGeometry.Open())
                {
                    ctx.BeginFigure(new Point(endX, yTop), true);
                    ctx.LineTo(new Point(endX - size / 2, yTop + size));
                    ctx.LineTo(new Point(endX + size / 2, yTop + size));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(markerGradient, new Pen(Brushes.White, 0.8), endGeometry);
            }
        }

        // 호버된 마커 툴팁
        if (_hoveredMarker != null)
        {
            DrawMarkerTooltip(context, _hoveredMarker);
        }
    }

    private void DrawMarkerTooltip(DrawingContext context, MarkerModel marker)
    {
        double x = TimeToX(marker.TimeMs);
        double y = 45;

        var typeface = new Typeface("Segoe UI");
        var text = new FormattedText(
            marker.Name,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            10,
            Brushes.White);

        // 배경 박스
        var padding = 4;
        var bgRect = new Rect(
            x - padding,
            y - padding,
            text.Width + padding * 2,
            text.Height + padding * 2);

        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(220, 40, 40, 40)),
            bgRect);

        context.DrawText(text, new Point(x, y));
    }

    private Color ArgbToColor(uint argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }

    private MarkerModel? GetMarkerAtPosition(Point point, double threshold = 20)
    {
        if (_viewModel == null) return null;

        foreach (var marker in _viewModel.Markers)
        {
            double x = TimeToX(marker.TimeMs);
            if (Math.Abs(point.X - x) < threshold && point.Y < 40)
                return marker;
        }

        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_viewModel == null) return;

        var point = e.GetPosition(this);
        var clickedMarker = GetMarkerAtPosition(point, threshold: 20);

        if (clickedMarker != null)
        {
            _viewModel.CurrentTimeMs = clickedMarker.TimeMs;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);
        var newHoveredMarker = GetMarkerAtPosition(point, threshold: 20);

        if (newHoveredMarker != _hoveredMarker)
        {
            _hoveredMarker = newHoveredMarker;
            InvalidateVisual();
        }
    }

    private double TimeToX(long timeMs)
    {
        return timeMs * _pixelsPerMs - _scrollOffsetX;
    }
}

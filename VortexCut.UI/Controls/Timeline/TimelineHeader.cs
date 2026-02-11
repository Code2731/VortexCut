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
    private const double TrackHeaderWidth = 60; // Grid Column 0 너비 (트랙 리스트)
    private TimelineViewModel? _viewModel;
    private double _pixelsPerMs = 0.1;
    private double _scrollOffsetX = 0;
    private MarkerModel? _hoveredMarker;
    private bool _isDraggingPlayhead = false;

    public void SetViewModel(TimelineViewModel viewModel)
    {
        _viewModel = viewModel;

        // 마커 추가/삭제 시 자동 새로고침
        _viewModel.Markers.CollectionChanged += (s, e) => InvalidateVisual();
    }

    public void SetZoom(double pixelsPerMs)
    {
        _pixelsPerMs = Math.Clamp(pixelsPerMs, 0.01, 5.0); // 최대 500%까지 확대
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
            DrawInOutPoints(context);
            DrawPlayhead(context); // Playhead 렌더링
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

        // SMPTE 타임코드 (HH:MM:SS:FF)
        int fps = 30;
        long totalFrames = (_viewModel.CurrentTimeMs * fps) / 1000;
        int frames = (int)(totalFrames % fps);
        int seconds = (int)((totalFrames / fps) % 60);
        int minutes = (int)((totalFrames / (fps * 60)) % 60);
        int hours = (int)(totalFrames / (fps * 3600));
        var timecodeText = $"{hours:D2}:{minutes:D2}:{seconds:D2}:{frames:D2}";

        // 클립 개수
        var clipCountText = $"Clips: {_viewModel.Clips.Count}";

        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
        var fontSize = 10.0;

        // 배경 박스 (프로페셔널 스타일)
        var infoText = $"{zoomText}  |  {timecodeText}  |  {clipCountText}";
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

    /// <summary>
    /// 줌 레벨에 적응하는 동적 시간 눈금
    /// - 줌 아웃: 10초/30초/1분 간격, 줌 인: 100ms/500ms 간격
    /// - 보이는 영역만 렌더링 (무한 타임라인 대응)
    /// </summary>
    private void DrawTimeRuler(DrawingContext context)
    {
        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
        var fontSize = 10;

        // 보이는 시간 범위 계산
        long visibleStartMs = Math.Max(0, XToTime(0));
        long visibleEndMs = XToTime(Bounds.Width) + 1000; // 여유분

        // 줌 레벨에 따라 최적 눈금 간격 선택 (주눈금 ~100-180px 간격 목표)
        long majorIntervalMs = CalculateMajorInterval();
        long minorIntervalMs = majorIntervalMs / 5;
        if (minorIntervalMs < 10) minorIntervalMs = 10; // 최소 10ms

        // 소눈금
        var smallTickPen = new Pen(new SolidColorBrush(Color.Parse("#555555")), 1);
        long firstMinor = (visibleStartMs / minorIntervalMs) * minorIntervalMs;
        for (long timeMs = firstMinor; timeMs <= visibleEndMs; timeMs += minorIntervalMs)
        {
            if (timeMs % majorIntervalMs == 0) continue; // 주눈금 위치는 건너뜀

            double x = TimeToX(timeMs);
            if (x < 0 || x > Bounds.Width) continue;

            context.DrawLine(smallTickPen,
                new Point(x, HeaderHeight - 5),
                new Point(x, HeaderHeight));
        }

        // 주눈금 + 시간 라벨
        var bigTickPen = new Pen(new SolidColorBrush(Color.Parse("#AAAAAA")), 1.5);
        long firstMajor = (visibleStartMs / majorIntervalMs) * majorIntervalMs;
        for (long timeMs = firstMajor; timeMs <= visibleEndMs; timeMs += majorIntervalMs)
        {
            double x = TimeToX(timeMs);
            if (x < 0 || x > Bounds.Width) continue;

            // 주눈금 (12px)
            context.DrawLine(bigTickPen,
                new Point(x, HeaderHeight - 12),
                new Point(x, HeaderHeight));

            // 시간 라벨
            var label = FormatTimeLabel(timeMs, majorIntervalMs);
            var text = new FormattedText(
                label,
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

    /// <summary>
    /// 줌 레벨에 따라 최적 주눈금 간격(ms) 계산
    /// 목표: 주눈금 사이 ~100-180px
    /// </summary>
    private long CalculateMajorInterval()
    {
        double targetPx = 140.0;
        double targetMs = targetPx / _pixelsPerMs;

        // "보기 좋은" 간격 목록 (ms)
        long[] nice = { 100, 200, 500, 1000, 2000, 5000, 10000, 15000,
                        30000, 60000, 120000, 300000, 600000, 1800000, 3600000 };

        foreach (var interval in nice)
        {
            if (interval >= targetMs) return interval;
        }
        return 3600000; // 1시간
    }

    /// <summary>
    /// 시간 라벨 포맷 (줌 레벨별 적응)
    /// 짧은 간격: "0.1s", "1.5s" / 긴 간격: "1:30", "10:00"
    /// </summary>
    private static string FormatTimeLabel(long timeMs, long intervalMs)
    {
        if (intervalMs < 1000)
        {
            // ms 단위: "0.0s", "0.5s" 등
            return $"{timeMs / 1000.0:F1}s";
        }
        if (timeMs < 60000)
        {
            // 1분 미만: "0s", "5s", "30s"
            return $"{timeMs / 1000}s";
        }
        // 1분 이상: "M:SS"
        int minutes = (int)(timeMs / 60000);
        int seconds = (int)((timeMs % 60000) / 1000);
        if (timeMs >= 3600000)
        {
            // 1시간 이상: "H:MM:SS"
            int hours = minutes / 60;
            minutes %= 60;
            return $"{hours}:{minutes:D2}:{seconds:D2}";
        }
        return $"{minutes}:{seconds:D2}";
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

            // Region 마커: 프로페셔널 스타일 범위 표시 (DaVinci Resolve 스타일)
            if (marker.IsRegion)
            {
                double endX = TimeToX(marker.EndTimeMs);

                // 범위 배경 (반투명 그라디언트)
                var regionRect = new Rect(x, yTop + size, endX - x, HeaderHeight - yTop - size);
                var regionBgGradient = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = new GradientStops
                    {
                        new GradientStop(Color.FromArgb(50, color.R, color.G, color.B), 0),
                        new GradientStop(Color.FromArgb(30, color.R, color.G, color.B), 0.5),
                        new GradientStop(Color.FromArgb(15, color.R, color.G, color.B), 1)
                    }
                };
                context.FillRectangle(regionBgGradient, regionRect);

                // 범위 테두리 (좌우 수직선)
                var edgePen = new Pen(
                    new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
                    1.5)
                {
                    DashStyle = new DashStyle(new double[] { 2, 2 }, 0)
                };
                context.DrawLine(edgePen,
                    new Point(x, yTop + size),
                    new Point(x, HeaderHeight));
                context.DrawLine(edgePen,
                    new Point(endX, yTop + size),
                    new Point(endX, HeaderHeight));

                // 상단 연결선 (더 두껍고 그라디언트 + 글로우)
                var lineGlowPen = new Pen(
                    new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
                    5);
                context.DrawLine(lineGlowPen, new Point(x, 30), new Point(endX, 30));

                var linePen = new Pen(
                    new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops = new GradientStops
                        {
                            new GradientStop(color, 0),
                            new GradientStop(Color.FromArgb(220, color.R, color.G, color.B), 0.5),
                            new GradientStop(color, 1)
                        }
                    },
                    3);
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

                // Region 라벨 (중앙에 표시)
                if (endX - x > 50 && !string.IsNullOrWhiteSpace(marker.Name))
                {
                    var labelTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
                    var label = new FormattedText(
                        marker.Name,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        labelTypeface,
                        10,
                        Brushes.White);

                    var labelX = x + (endX - x) / 2 - label.Width / 2;
                    var labelY = 35;

                    // 라벨 배경
                    var labelBgRect = new Rect(labelX - 4, labelY - 2, label.Width + 8, label.Height + 4);
                    var labelBgGradient = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops = new GradientStops
                        {
                            new GradientStop(Color.FromArgb(220, color.R, color.G, color.B), 0),
                            new GradientStop(Color.FromArgb(200, (byte)Math.Max(0, color.R - 40),
                                (byte)Math.Max(0, color.G - 40), (byte)Math.Max(0, color.B - 40)), 1)
                        }
                    };
                    context.FillRectangle(labelBgGradient, labelBgRect);
                    context.DrawRectangle(new Pen(Brushes.White, 1), labelBgRect);

                    context.DrawText(label, new Point(labelX, labelY));
                }
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

        // 재생 중이면 즉시 중지 (타이머+클럭 정지)
        if (_viewModel.IsPlaying)
        {
            _viewModel.IsPlaying = false;
            _viewModel.RequestStopPlayback?.Invoke();
        }

        var point = e.GetPosition(this);
        var clickedMarker = GetMarkerAtPosition(point, threshold: 20);

        if (clickedMarker != null)
        {
            _viewModel.CurrentTimeMs = clickedMarker.TimeMs;
            e.Handled = true;
        }
        else
        {
            // 마커가 아닌 곳을 클릭하면 Playhead 이동
            long timeMs = XToTime(point.X);
            _viewModel.CurrentTimeMs = Math.Max(0, timeMs);
            _isDraggingPlayhead = true;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);

        // Playhead 드래그 중이면 계속 업데이트
        if (_isDraggingPlayhead && _viewModel != null)
        {
            long timeMs = XToTime(point.X);
            _viewModel.CurrentTimeMs = Math.Max(0, timeMs);
        }

        var newHoveredMarker = GetMarkerAtPosition(point, threshold: 20);

        if (newHoveredMarker != _hoveredMarker)
        {
            _hoveredMarker = newHoveredMarker;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDraggingPlayhead = false;
    }

    /// <summary>
    /// In/Out 포인트 시각화 (워크에어리어 표시)
    /// </summary>
    private void DrawInOutPoints(DrawingContext context)
    {
        if (_viewModel == null) return;

        // In/Out 포인트가 둘 다 있으면 워크에어리어 영역 하이라이트
        if (_viewModel.InPointMs.HasValue && _viewModel.OutPointMs.HasValue)
        {
            double inX = TimeToX(_viewModel.InPointMs.Value);
            double outX = TimeToX(_viewModel.OutPointMs.Value);

            // 워크에어리어 배경 (반투명 파란색)
            var workAreaRect = new Rect(
                Math.Max(0, inX),
                0,
                Math.Min(Bounds.Width, outX) - Math.Max(0, inX),
                HeaderHeight);

            if (workAreaRect.Width > 0)
            {
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(30, 0, 122, 204)),
                    workAreaRect);
            }
        }

        // In 포인트 표시 (초록색 삼각형)
        if (_viewModel.InPointMs.HasValue)
        {
            double inX = TimeToX(_viewModel.InPointMs.Value);

            if (inX >= 0 && inX <= Bounds.Width)
            {
                // 그림자
                var shadowGeometry = new StreamGeometry();
                using (var ctx = shadowGeometry.Open())
                {
                    ctx.BeginFigure(new Point(inX + 1, 1), true);
                    ctx.LineTo(new Point(inX + 11, 1));
                    ctx.LineTo(new Point(inX + 1, 11));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                    null,
                    shadowGeometry);

                // 본체
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(inX, 0), true);
                    ctx.LineTo(new Point(inX + 10, 0));
                    ctx.LineTo(new Point(inX, 10));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(220, 92, 184, 92)), // 초록색
                    new Pen(Brushes.White, 1),
                    geometry);

                // In 라벨
                var inText = new FormattedText(
                    "IN",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold),
                    8,
                    Brushes.White);

                context.DrawText(inText, new Point(inX + 2, 12));
            }
        }

        // Out 포인트 표시 (빨간색 삼각형)
        if (_viewModel.OutPointMs.HasValue)
        {
            double outX = TimeToX(_viewModel.OutPointMs.Value);

            if (outX >= 0 && outX <= Bounds.Width)
            {
                // 그림자
                var shadowGeometry = new StreamGeometry();
                using (var ctx = shadowGeometry.Open())
                {
                    ctx.BeginFigure(new Point(outX + 1, 1), true);
                    ctx.LineTo(new Point(outX - 9, 1));
                    ctx.LineTo(new Point(outX + 1, 11));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                    null,
                    shadowGeometry);

                // 본체
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(outX, 0), true);
                    ctx.LineTo(new Point(outX - 10, 0));
                    ctx.LineTo(new Point(outX, 10));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(220, 217, 83, 79)), // 빨간색
                    new Pen(Brushes.White, 1),
                    geometry);

                // Out 라벨
                var outText = new FormattedText(
                    "OUT",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold),
                    8,
                    Brushes.White);

                context.DrawText(outText, new Point(outX - outText.Width - 2, 12));
            }
        }
    }

    /// <summary>
    /// Playhead (재생 헤드) 시각화
    /// </summary>
    private void DrawPlayhead(DrawingContext context)
    {
        if (_viewModel == null) return;

        double x = TimeToX(_viewModel.CurrentTimeMs);

        // 화면 밖이면 렌더링하지 않음
        if (x < 0 || x > Bounds.Width)
            return;

        bool isPlaying = _viewModel.IsPlaying;
        var baseColor = Color.FromRgb(255, 90, 0); // 주황색 (#FF5A00)

        // 재생 중일 때 글로우 애니메이션
        if (isPlaying)
        {
            // 글로우 레이어 (넓은 반투명)
            var glowPen = new Pen(
                new SolidColorBrush(Color.FromArgb(80, baseColor.R, baseColor.G, baseColor.B)),
                6);
            context.DrawLine(glowPen,
                new Point(x, 0),
                new Point(x, HeaderHeight));

            // 중간 글로우 (좁은 반투명)
            var midGlowPen = new Pen(
                new SolidColorBrush(Color.FromArgb(120, baseColor.R, baseColor.G, baseColor.B)),
                3);
            context.DrawLine(midGlowPen,
                new Point(x, 0),
                new Point(x, HeaderHeight));
        }

        // Playhead 수직선 (메인)
        var linePen = new Pen(new SolidColorBrush(baseColor), 2);
        context.DrawLine(linePen,
            new Point(x, 0),
            new Point(x, HeaderHeight));

        // 상단 핸들 (삼각형 + 사각형 드래그 영역)
        const double handleWidth = 16;
        const double handleHeight = 12;

        // 핸들 그림자
        var shadowGeometry = new StreamGeometry();
        using (var ctx = shadowGeometry.Open())
        {
            ctx.BeginFigure(new Point(x + 1, 1), true);
            ctx.LineTo(new Point(x - handleWidth / 2 + 1, handleHeight + 1));
            ctx.LineTo(new Point(x + handleWidth / 2 + 1, handleHeight + 1));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            null,
            shadowGeometry);

        // 핸들 본체 (그라디언트)
        var handleGeometry = new StreamGeometry();
        using (var ctx = handleGeometry.Open())
        {
            ctx.BeginFigure(new Point(x, 0), true);
            ctx.LineTo(new Point(x - handleWidth / 2, handleHeight));
            ctx.LineTo(new Point(x + handleWidth / 2, handleHeight));
            ctx.EndFigure(true);
        }

        var handleGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(baseColor, 0),
                new GradientStop(Color.FromRgb(
                    (byte)Math.Max(0, baseColor.R - 50),
                    (byte)Math.Max(0, baseColor.G - 50),
                    (byte)Math.Max(0, baseColor.B - 50)), 1)
            }
        };

        context.DrawGeometry(
            handleGradient,
            new Pen(Brushes.White, 1.5),
            handleGeometry);

        // 재생 중 표시 (핸들 안에 작은 삼각형)
        if (isPlaying)
        {
            var playIconGeometry = new StreamGeometry();
            using (var ctx = playIconGeometry.Open())
            {
                ctx.BeginFigure(new Point(x - 3, 4), true);
                ctx.LineTo(new Point(x + 3, 7));
                ctx.LineTo(new Point(x - 3, 10));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(
                Brushes.White,
                null,
                playIconGeometry);
        }
    }

    private double TimeToX(long timeMs)
    {
        // TrackHeaderWidth 오프셋: Header는 ColumnSpan=2라 x=0이 Grid 왼쪽 끝이지만,
        // ClipCanvasPanel은 Column=1 (x=60)부터 시작하므로 60px 보정 필요
        return timeMs * _pixelsPerMs - _scrollOffsetX + TrackHeaderWidth;
    }

    private long XToTime(double x)
    {
        return (long)((x - TrackHeaderWidth + _scrollOffsetX) / _pixelsPerMs);
    }
}

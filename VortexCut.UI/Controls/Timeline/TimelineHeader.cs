using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 타임라인 상단 헤더 (시간 눈금, 마커, 줌 슬라이더)
/// DaVinci Resolve 스타일 디자인 (Phase A 리디자인)
/// </summary>
public class TimelineHeader : Control
{
    // Grid Row 높이 60px과 동기화 (A-1)
    private const double HeaderHeight = 60;
    private const double TrackHeaderWidth = 60; // Grid Column 0 너비 (트랙 리스트)

    // 줌 슬라이더 상수 (A-4) - 트랙 헤더 컬럼(0~60px) 안에 배치
    private const double ZoomBarX = 6;
    private const double ZoomBarWidth = 48;
    private const double ZoomBarHeight = 5;
    private const double ZoomBarY = 48; // 헤더 하단
    private const double ZoomMinPxPerMs = 0.001;
    private const double ZoomMaxPxPerMs = 1.0;

    // 상태
    private TimelineViewModel? _viewModel;
    private double _pixelsPerMs = 0.1;
    private double _scrollOffsetX = 0;
    private MarkerModel? _hoveredMarker;
    private bool _isDraggingPlayhead = false;
    private bool _isDraggingZoom = false;

    /// <summary>
    /// 줌 변경 콜백 (TimelineCanvas에서 설정)
    /// </summary>
    public Action<double>? OnZoomChanged { get; set; }

    public void SetViewModel(TimelineViewModel viewModel)
    {
        _viewModel = viewModel;
        // 마커 추가/삭제 시 자동 새로고침
        _viewModel.Markers.CollectionChanged += (s, e) => InvalidateVisual();
    }

    public void SetZoom(double pixelsPerMs)
    {
        _pixelsPerMs = Math.Clamp(pixelsPerMs, 0.001, 5.0);
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

        // 배경 그라디언트 (캐시된 불변 브러시 A-5)
        var headerRect = new Rect(0, 0, Bounds.Width, HeaderHeight);
        context.FillRectangle(RenderResourceCache.HeaderBgGradient, headerRect);

        // 하단 하이라이트 라인 (캐시 A-5)
        context.DrawLine(RenderResourceCache.HeaderHighlightPen,
            new Point(0, HeaderHeight - 1),
            new Point(Bounds.Width, HeaderHeight - 1));

        // 시간 눈금 (A-2 리디자인)
        DrawTimeRuler(context);

        if (_viewModel != null)
        {
            DrawMarkers(context);
            DrawInOutPoints(context);
            DrawPlayhead(context);
        }

        // 상태 정보 (우상단, A-3 동적 FPS)
        DrawStatusInfo(context);

        // 줌 슬라이더 (좌하단, A-4 인터랙티브)
        DrawZoomSlider(context);
    }

    /// <summary>
    /// 상태 정보 (우상단): 줌%, SMPTE 타임코드, 클립 수
    /// </summary>
    private void DrawStatusInfo(DrawingContext context)
    {
        if (_viewModel == null) return;

        // SMPTE 타임코드 (A-3: 동적 FPS)
        int fps = _viewModel.ProjectFps;
        long totalFrames = (_viewModel.CurrentTimeMs * fps) / 1000;
        int frames = (int)(totalFrames % fps);
        int seconds = (int)((totalFrames / fps) % 60);
        int minutes = (int)((totalFrames / (fps * 60)) % 60);
        int hours = (int)(totalFrames / (fps * 3600));
        var timecodeText = $"{hours:D2}:{minutes:D2}:{seconds:D2}:{frames:D2}";

        var infoText = $"{timecodeText}  |  {_viewModel.Clips.Count} clips";
        var text = new FormattedText(
            infoText,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            RenderResourceCache.SegoeUISemiBold, // 캐시 (A-5)
            10.0,
            RenderResourceCache.WhiteBrush);

        var textX = Bounds.Width - text.Width - 12;
        var textY = 3.0;

        // 배경 (캐시된 브러시 A-5)
        var bgRect = new Rect(textX - 6, textY - 2, text.Width + 12, text.Height + 4);
        context.FillRectangle(RenderResourceCache.StatusInfoBgBrush, bgRect);
        context.DrawRectangle(RenderResourceCache.StatusInfoBorderPen, bgRect);

        context.DrawText(text, new Point(textX, textY));
    }

    /// <summary>
    /// 줌 슬라이더 (좌하단, 인터랙티브 A-4)
    /// 클릭/드래그로 줌 레벨 조절, 더블클릭으로 맞춤 보기
    /// </summary>
    private void DrawZoomSlider(DrawingContext context)
    {
        // 줌 정규화 (로그 스케일)
        double normalizedZoom = GetNormalizedZoom();

        // 배경 트랙 (캐시 A-5)
        var trackRect = new Rect(ZoomBarX, ZoomBarY, ZoomBarWidth, ZoomBarHeight);
        context.FillRectangle(RenderResourceCache.ZoomTrackBrush, trackRect);
        context.DrawRectangle(RenderResourceCache.ZoomTrackBorderPen, trackRect);

        // 채워진 부분 (줌 레벨, 캐시된 그라디언트 A-5)
        var fillWidth = ZoomBarWidth * normalizedZoom;
        if (fillWidth > 0)
        {
            var fillRect = new Rect(ZoomBarX, ZoomBarY, fillWidth, ZoomBarHeight);
            context.FillRectangle(RenderResourceCache.ZoomFillGradient, fillRect);
        }

        // 글로우 효과 (캐시 A-5)
        if (fillWidth > 0)
        {
            var glowRect = new Rect(ZoomBarX, ZoomBarY - 1, fillWidth, ZoomBarHeight + 2);
            context.FillRectangle(RenderResourceCache.ZoomGlowBrush, glowRect);
        }

        // 썸 (현재 위치 핸들)
        var thumbX = ZoomBarX + fillWidth;
        var thumbRect = new Rect(thumbX - 3, ZoomBarY - 2, 6, ZoomBarHeight + 4);
        context.FillRectangle(RenderResourceCache.ZoomThumbBrush, thumbRect);
        context.DrawRectangle(RenderResourceCache.ZoomThumbBorderPen, thumbRect);

        // 줌 % 표시 (슬라이더 위, 0.1 px/ms = 100% 기준)
        var zoomPct = (int)(_pixelsPerMs / 0.1 * 100);
        var label = new FormattedText(
            $"{zoomPct}%",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            RenderResourceCache.SegoeUIBold,
            8,
            RenderResourceCache.ZoomLabelBrush);

        context.DrawText(label, new Point(ZoomBarX, ZoomBarY - 11));
    }

    /// <summary>
    /// DaVinci Resolve 스타일 시간 눈금 (A-2 리디자인)
    /// - 통일된 M:SS 라벨 포맷
    /// - 줌 레벨 적응 동적 눈금 높이
    /// - 텍스트 배경 제거 (깔끔한 직접 표시)
    /// </summary>
    private void DrawTimeRuler(DrawingContext context)
    {
        // 보이는 시간 범위
        long visibleStartMs = Math.Max(0, XToTime(0));
        long visibleEndMs = XToTime(Bounds.Width) + 1000;

        // 줌 레벨에 따라 최적 눈금 간격 선택
        long majorIntervalMs = CalculateMajorInterval();
        long minorIntervalMs = majorIntervalMs / 5;
        if (minorIntervalMs < 10) minorIntervalMs = 10;

        // 소눈금 (캐시된 펜 A-5)
        long firstMinor = (visibleStartMs / minorIntervalMs) * minorIntervalMs;
        for (long timeMs = firstMinor; timeMs <= visibleEndMs; timeMs += minorIntervalMs)
        {
            if (timeMs % majorIntervalMs == 0) continue;

            double x = TimeToX(timeMs);
            if (x < TrackHeaderWidth - 5 || x > Bounds.Width) continue;

            // 줌 레벨에 따른 동적 소눈금 높이 (A-2)
            double minorHeight = _pixelsPerMs > 0.3 ? 8 : (_pixelsPerMs > 0.05 ? 6 : 4);

            context.DrawLine(RenderResourceCache.MinorTickPen,
                new Point(x, HeaderHeight - 2 - minorHeight),
                new Point(x, HeaderHeight - 2));
        }

        // 서브소눈금 점 (고줌 전용 — IMG.LY 스타일)
        if (_pixelsPerMs > 0.3)
        {
            long subMinorMs = minorIntervalMs / 2;
            if (subMinorMs >= 5)
            {
                long firstSub = (visibleStartMs / subMinorMs) * subMinorMs;
                for (long t = firstSub; t <= visibleEndMs; t += subMinorMs)
                {
                    if (t % minorIntervalMs == 0) continue; // 소눈금과 겹치면 스킵
                    double sx = TimeToX(t);
                    if (sx < TrackHeaderWidth - 5 || sx > Bounds.Width) continue;
                    context.DrawEllipse(
                        RenderResourceCache.SubMinorTickDotBrush, null,
                        new Point(sx, HeaderHeight - 4), 1.2, 1.2);
                }
            }
        }

        // 프레임 번호 표시 (초고줌 전용)
        if (_pixelsPerMs > 0.5 && _viewModel != null)
        {
            double fps = 30.0; // 기본 30fps
            long frameIntervalMs = Math.Max(1, (long)(1000.0 / fps));
            long firstFrame = (visibleStartMs / frameIntervalMs) * frameIntervalMs;
            double lastFrameX = -100;
            for (long t = firstFrame; t <= visibleEndMs; t += frameIntervalMs)
            {
                if (t % majorIntervalMs == 0) continue;
                double fx = TimeToX(t);
                if (fx < TrackHeaderWidth - 5 || fx > Bounds.Width) continue;
                if (fx - lastFrameX < 20) continue; // 겹침 방지
                int frameNum = (int)((t * fps / 1000.0) % fps);
                var frameText = new FormattedText(
                    $":{frameNum:D2}",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    RenderResourceCache.SegoeUI, 8,
                    RenderResourceCache.FrameNumberBrush);
                context.DrawText(frameText, new Point(fx + 1, HeaderHeight - 26));
                lastFrameX = fx;
            }
        }

        // 주눈금 + 시간 라벨 (캐시된 펜 A-5)
        long firstMajor = (visibleStartMs / majorIntervalMs) * majorIntervalMs;
        for (long timeMs = firstMajor; timeMs <= visibleEndMs; timeMs += majorIntervalMs)
        {
            double x = TimeToX(timeMs);
            if (x < TrackHeaderWidth - 5 || x > Bounds.Width) continue;

            // 주눈금 (14px, 캐시 A-5)
            context.DrawLine(RenderResourceCache.MajorTickPen,
                new Point(x, HeaderHeight - 16),
                new Point(x, HeaderHeight - 2));

            // 시간 라벨 (A-2: 통일된 M:SS 포맷, 배경 없이 직접 표시)
            var label = FormatTimeLabel(timeMs, majorIntervalMs);
            var text = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUISemiBold, // 캐시 (A-5)
                11,
                RenderResourceCache.WhiteBrush);

            // 라벨을 눈금 바로 위에 표시 (A-2: 배경 박스 제거)
            context.DrawText(text, new Point(x + 3, HeaderHeight - 30));
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

        long[] nice = { 100, 200, 500, 1000, 2000, 5000, 10000, 15000,
                        30000, 60000, 120000, 300000, 600000, 1800000, 3600000 };

        foreach (var interval in nice)
        {
            if (interval >= targetMs) return interval;
        }
        return 3600000;
    }

    /// <summary>
    /// 시간 라벨 포맷 (A-2: DaVinci Resolve 스타일 통일 포맷)
    /// - sub-second: "0:00.1", "0:00.5"
    /// - under 1min: "0:05", "0:30"
    /// - under 1hr: "1:30", "5:00"
    /// - over 1hr: "1:00:00"
    /// </summary>
    private static string FormatTimeLabel(long timeMs, long intervalMs)
    {
        if (intervalMs < 1000)
        {
            // 1초 미만 간격: 고정밀 소수점 초 표시
            int mins = (int)(timeMs / 60000);
            double secs = (timeMs % 60000) / 1000.0;
            if (intervalMs <= 200)
                return $"{mins}:{secs:00.00}"; // 초고줌: "0:03.50"
            return $"{mins}:{secs:00.0}";       // 일반줌: "0:03.5"
        }

        long totalSecs = timeMs / 1000;
        int minutes = (int)(totalSecs / 60);
        int seconds = (int)(totalSecs % 60);

        if (timeMs >= 3600000)
        {
            // 1시간 이상: "1:00:00"
            int hours = minutes / 60;
            minutes %= 60;
            return $"{hours}:{minutes:D2}:{seconds:D2}";
        }

        // 기본: "0:05", "0:30", "1:30", "5:00"
        return $"{minutes}:{seconds:D2}";
    }

    /// <summary>
    /// 마커 렌더링 (A-1: Y좌표 조정)
    /// </summary>
    private void DrawMarkers(DrawingContext context)
    {
        if (_viewModel == null) return;

        foreach (var marker in _viewModel.Markers)
        {
            double x = TimeToX(marker.TimeMs);
            if (x < -20 || x > Bounds.Width + 20)
                continue;

            var color = ArgbToColor(marker.ColorArgb);
            var size = marker.Type == MarkerType.Chapter ? 12.0 : 8.0;
            var yTop = 6.0; // A-1: 60px 헤더에 맞춤 (80px일 때 18 → 6)

            // 마커 그림자 (캐시 A-5)
            var shadowGeometry = new StreamGeometry();
            using (var ctx = shadowGeometry.Open())
            {
                ctx.BeginFigure(new Point(x + 1, yTop + 1), true);
                ctx.LineTo(new Point(x - size / 2 + 1, yTop + size + 1));
                ctx.LineTo(new Point(x + size / 2 + 1, yTop + size + 1));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(
                RenderResourceCache.HeaderPlayheadShadowBrush, // 캐시 (A-5)
                null,
                shadowGeometry);

            // 마커 본체 (동적 그라디언트 - 풀 사용 A-5)
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x, yTop), true);
                ctx.LineTo(new Point(x - size / 2, yTop + size));
                ctx.LineTo(new Point(x + size / 2, yTop + size));
                ctx.EndFigure(true);
            }

            var markerGradient = RenderResourceCache.GetVerticalGradient(
                color,
                Color.FromRgb(
                    (byte)Math.Max(0, color.R - 40),
                    (byte)Math.Max(0, color.G - 40),
                    (byte)Math.Max(0, color.B - 40)));

            var markerBorderPen = RenderResourceCache.GetPen(Colors.White, 0.8);
            context.DrawGeometry(markerGradient, markerBorderPen, geometry);

            // Region 마커
            if (marker.IsRegion)
            {
                DrawRegionMarker(context, marker, x, yTop, size, color, markerGradient);
            }
        }

        // 호버된 마커 툴팁
        if (_hoveredMarker != null)
        {
            DrawMarkerTooltip(context, _hoveredMarker);
        }
    }

    /// <summary>
    /// Region 마커 범위 표시 (A-1: Y좌표 조정, A-5: 캐시)
    /// </summary>
    private void DrawRegionMarker(DrawingContext context, MarkerModel marker,
        double x, double yTop, double size, Color color, IBrush markerGradient)
    {
        double endX = TimeToX(marker.EndTimeMs);

        // 범위 배경 (동적 색상 → 풀 사용 A-5)
        var regionRect = new Rect(x, yTop + size, endX - x, HeaderHeight - yTop - size - 2);
        var regionBg = RenderResourceCache.GetSolidBrush(
            Color.FromArgb(35, color.R, color.G, color.B));
        context.FillRectangle(regionBg, regionRect);

        // 범위 좌우 수직선 (동적 색상 A-5)
        var edgePen = RenderResourceCache.GetPen(
            Color.FromArgb(80, color.R, color.G, color.B), 1);
        context.DrawLine(edgePen,
            new Point(x, yTop + size),
            new Point(x, HeaderHeight - 2));
        context.DrawLine(edgePen,
            new Point(endX, yTop + size),
            new Point(endX, HeaderHeight - 2));

        // 상단 연결선
        double lineY = yTop + size + 4;
        var lineGlowPen = RenderResourceCache.GetPen(
            Color.FromArgb(60, color.R, color.G, color.B), 4);
        context.DrawLine(lineGlowPen, new Point(x, lineY), new Point(endX, lineY));

        var linePen = RenderResourceCache.GetPen(color, 2);
        context.DrawLine(linePen, new Point(x, lineY), new Point(endX, lineY));

        // 끝 삼각형
        var endShadowGeometry = new StreamGeometry();
        using (var ctx = endShadowGeometry.Open())
        {
            ctx.BeginFigure(new Point(endX + 1, yTop + 1), true);
            ctx.LineTo(new Point(endX - size / 2 + 1, yTop + size + 1));
            ctx.LineTo(new Point(endX + size / 2 + 1, yTop + size + 1));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(
            RenderResourceCache.HeaderPlayheadShadowBrush,
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
        var endBorderPen = RenderResourceCache.GetPen(Colors.White, 0.8);
        context.DrawGeometry(markerGradient, endBorderPen, endGeometry);

        // Region 라벨 (충분한 공간이 있을 때만)
        if (endX - x > 50 && !string.IsNullOrWhiteSpace(marker.Name))
        {
            var label = new FormattedText(
                marker.Name,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUISemiBold, // 캐시 (A-5)
                9,
                RenderResourceCache.WhiteBrush);

            double labelX = x + (endX - x) / 2 - label.Width / 2;
            double labelY = lineY + 3;

            // 라벨 배경 (동적 색상 A-5)
            var labelBgRect = new Rect(labelX - 3, labelY - 1, label.Width + 6, label.Height + 2);
            var labelBg = RenderResourceCache.GetSolidBrush(
                Color.FromArgb(200, color.R, color.G, color.B));
            context.FillRectangle(labelBg, labelBgRect);
            context.DrawText(label, new Point(labelX, labelY));
        }
    }

    /// <summary>
    /// 마커 툴팁 (A-1: Y좌표 조정, A-5: 캐시)
    /// </summary>
    private void DrawMarkerTooltip(DrawingContext context, MarkerModel marker)
    {
        double x = TimeToX(marker.TimeMs);
        double y = 22.0; // A-1: 60px 기준

        var text = new FormattedText(
            marker.Name,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            RenderResourceCache.SegoeUI, // 캐시 (A-5)
            10,
            RenderResourceCache.WhiteBrush);

        var padding = 4;
        var bgRect = new Rect(x - padding, y - padding, text.Width + padding * 2, text.Height + padding * 2);
        context.FillRectangle(RenderResourceCache.MarkerTooltipBgBrush, bgRect); // 캐시 (A-5)
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
            if (Math.Abs(point.X - x) < threshold && point.Y < 25)
                return marker;
        }
        return null;
    }

    /// <summary>
    /// 줌 슬라이더 영역 히트테스트
    /// </summary>
    private bool IsInZoomBar(Point point)
    {
        return point.X >= ZoomBarX - 4 &&
               point.X <= ZoomBarX + ZoomBarWidth + 4 &&
               point.Y >= ZoomBarY - 4 &&
               point.Y <= ZoomBarY + ZoomBarHeight + 4;
    }

    /// <summary>
    /// 클릭 X 위치에서 줌 레벨 계산 (로그 스케일)
    /// </summary>
    private double CalculateZoomFromX(double clickX)
    {
        double normalized = Math.Clamp((clickX - ZoomBarX) / ZoomBarWidth, 0, 1);
        // 로그 스케일: 0→minZoom, 1→maxZoom
        double logMin = Math.Log10(ZoomMinPxPerMs);
        double logMax = Math.Log10(ZoomMaxPxPerMs);
        return Math.Pow(10, normalized * (logMax - logMin) + logMin);
    }

    /// <summary>
    /// 현재 줌 레벨의 정규화된 값 (0~1, 로그 스케일)
    /// </summary>
    private double GetNormalizedZoom()
    {
        double logMin = Math.Log10(ZoomMinPxPerMs);
        double logMax = Math.Log10(ZoomMaxPxPerMs);
        double normalized = (Math.Log10(_pixelsPerMs) - logMin) / (logMax - logMin);
        return Math.Clamp(normalized, 0, 1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_viewModel == null) return;

        var point = e.GetPosition(this);

        // 줌 슬라이더 클릭 (A-4: 인터랙티브)
        if (IsInZoomBar(point))
        {
            // 더블클릭: 맞춤 보기 (전체 타임라인 피팅)
            if (e.ClickCount == 2)
            {
                FitToWindow();
            }
            else
            {
                // 클릭/드래그 줌 조절
                _isDraggingZoom = true;
                ApplyZoomFromPointer(point.X);
            }
            e.Handled = true;
            return;
        }

        // 재생 중이면 즉시 중지
        if (_viewModel.IsPlaying)
        {
            _viewModel.IsPlaying = false;
            _viewModel.RequestStopPlayback?.Invoke();
        }

        // 마커 클릭
        var clickedMarker = GetMarkerAtPosition(point, threshold: 15);
        if (clickedMarker != null)
        {
            _viewModel.CurrentTimeMs = clickedMarker.TimeMs;
            e.Handled = true;
        }
        else
        {
            // Playhead 이동 (드래그 시작)
            long timeMs = XToTime(point.X);
            _viewModel.CurrentTimeMs = ClampTimeToContent(Math.Max(0, timeMs));
            _isDraggingPlayhead = true;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        // 줌 드래그 중 (A-4)
        if (_isDraggingZoom)
        {
            ApplyZoomFromPointer(point.X);
            return;
        }

        // Playhead 드래그 중
        if (_isDraggingPlayhead && _viewModel != null)
        {
            long timeMs = XToTime(point.X);
            _viewModel.CurrentTimeMs = ClampTimeToContent(Math.Max(0, timeMs));
        }

        // 마커 호버
        var newHoveredMarker = GetMarkerAtPosition(point, threshold: 15);
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
        _isDraggingZoom = false;
    }

    /// <summary>
    /// Ctrl + 마우스 휠로 줌 인/아웃 지원
    /// (Kdenlive, Resolve 스타일 타임라인 줌 UX)
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        // 한 번 휠당 약 15% 비율로 확대/축소
        double deltaSteps = e.Delta.Y; // 위로 스크롤: 양수, 아래: 음수
        if (Math.Abs(deltaSteps) < double.Epsilon)
            return;

        double zoomFactorPerStep = 1.15;
        double factor = deltaSteps > 0
            ? zoomFactorPerStep
            : 1.0 / zoomFactorPerStep;

        // 현재 마우스 위치를 기준으로 줌 (해당 포인트가 최대한 고정되도록)
        var point = e.GetPosition(this);
        double normalizedBefore = GetNormalizedZoom();

        // px/ms 기준으로 배율 적용
        double newPixelsPerMs = Math.Clamp(_pixelsPerMs * factor, 0.001, 5.0);
        _pixelsPerMs = newPixelsPerMs;
        OnZoomChanged?.Invoke(_pixelsPerMs);
        InvalidateVisual();

        e.Handled = true;
    }

    /// <summary>
    /// 포인터 X 위치로 줌 적용 (A-4)
    /// </summary>
    private void ApplyZoomFromPointer(double pointerX)
    {
        double newZoom = CalculateZoomFromX(pointerX);
        _pixelsPerMs = Math.Clamp(newZoom, 0.001, 5.0);
        OnZoomChanged?.Invoke(_pixelsPerMs);
        InvalidateVisual();
    }

    /// <summary>
    /// 맞춤 보기: 전체 타임라인이 화면에 들어오도록 줌 조절 (A-4)
    /// </summary>
    private void FitToWindow()
    {
        if (_viewModel == null || _viewModel.Clips.Count == 0) return;

        // 최대 클립 끝 시간
        long maxEndMs = 0;
        foreach (var clip in _viewModel.Clips)
        {
            long end = clip.StartTimeMs + clip.DurationMs;
            if (end > maxEndMs) maxEndMs = end;
        }

        if (maxEndMs <= 0) return;

        // 여유 10% 추가
        double availableWidth = Bounds.Width - TrackHeaderWidth - 20;
        if (availableWidth <= 0) return;

        double fitZoom = availableWidth / (maxEndMs * 1.1);
        _pixelsPerMs = Math.Clamp(fitZoom, 0.001, 5.0);
        OnZoomChanged?.Invoke(_pixelsPerMs);
        InvalidateVisual();
    }

    /// <summary>
    /// In/Out 포인트 시각화 (A-1: Y좌표, A-5: 캐시)
    /// </summary>
    private void DrawInOutPoints(DrawingContext context)
    {
        if (_viewModel == null) return;

        // 워크에어리어 배경
        if (_viewModel.InPointMs.HasValue && _viewModel.OutPointMs.HasValue)
        {
            double inX = TimeToX(_viewModel.InPointMs.Value);
            double outX = TimeToX(_viewModel.OutPointMs.Value);

            var workAreaRect = new Rect(
                Math.Max(0, inX), 0,
                Math.Min(Bounds.Width, outX) - Math.Max(0, inX),
                HeaderHeight);

            if (workAreaRect.Width > 0)
            {
                context.FillRectangle(RenderResourceCache.WorkAreaBrush, workAreaRect);
            }
        }

        // In 포인트 (초록색 삼각형)
        if (_viewModel.InPointMs.HasValue)
        {
            double inX = TimeToX(_viewModel.InPointMs.Value);
            if (inX >= 0 && inX <= Bounds.Width)
            {
                DrawInOutTriangle(context, inX, isInPoint: true);
            }
        }

        // Out 포인트 (빨간색 삼각형)
        if (_viewModel.OutPointMs.HasValue)
        {
            double outX = TimeToX(_viewModel.OutPointMs.Value);
            if (outX >= 0 && outX <= Bounds.Width)
            {
                DrawInOutTriangle(context, outX, isInPoint: false);
            }
        }
    }

    /// <summary>
    /// In/Out 삼각형 그리기 (A-5: 캐시)
    /// </summary>
    private void DrawInOutTriangle(DrawingContext context, double x, bool isInPoint)
    {
        double triSize = 8; // A-1: 약간 축소 (10→8)

        // 그림자
        var shadowBrush = RenderResourceCache.GetSolidBrush(Color.FromArgb(100, 0, 0, 0));
        var shadowGeometry = new StreamGeometry();
        using (var ctx = shadowGeometry.Open())
        {
            if (isInPoint)
            {
                ctx.BeginFigure(new Point(x + 1, 1), true);
                ctx.LineTo(new Point(x + triSize + 1, 1));
                ctx.LineTo(new Point(x + 1, triSize + 1));
            }
            else
            {
                ctx.BeginFigure(new Point(x + 1, 1), true);
                ctx.LineTo(new Point(x - triSize + 1, 1));
                ctx.LineTo(new Point(x + 1, triSize + 1));
            }
            ctx.EndFigure(true);
        }
        context.DrawGeometry(shadowBrush, null, shadowGeometry);

        // 본체
        var fillBrush = isInPoint ? RenderResourceCache.InPointBrush : RenderResourceCache.OutPointBrush;
        var borderPen = RenderResourceCache.GetPen(Colors.White, 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (isInPoint)
            {
                ctx.BeginFigure(new Point(x, 0), true);
                ctx.LineTo(new Point(x + triSize, 0));
                ctx.LineTo(new Point(x, triSize));
            }
            else
            {
                ctx.BeginFigure(new Point(x, 0), true);
                ctx.LineTo(new Point(x - triSize, 0));
                ctx.LineTo(new Point(x, triSize));
            }
            ctx.EndFigure(true);
        }
        context.DrawGeometry(fillBrush, borderPen, geometry);

        // 라벨 (캐시 A-5)
        var labelText = isInPoint ? "IN" : "OUT";
        var label = new FormattedText(
            labelText,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            RenderResourceCache.SegoeUIBold, // 캐시 (A-5)
            7,
            RenderResourceCache.WhiteBrush);

        double labelX = isInPoint ? x + 2 : x - label.Width - 2;
        context.DrawText(label, new Point(labelX, triSize + 1));
    }

    /// <summary>
    /// Playhead 시각화 (A-1: Y좌표, A-5: 캐시)
    /// </summary>
    private void DrawPlayhead(DrawingContext context)
    {
        if (_viewModel == null) return;

        double x = TimeToX(_viewModel.CurrentTimeMs);
        if (x < 0 || x > Bounds.Width)
            return;

        bool isPlaying = _viewModel.IsPlaying;

        // 재생 중 글로우 (캐시 A-5)
        if (isPlaying)
        {
            context.DrawLine(RenderResourceCache.HeaderPlayheadGlowPen,
                new Point(x, 0), new Point(x, HeaderHeight));
            context.DrawLine(RenderResourceCache.HeaderPlayheadMidGlowPen,
                new Point(x, 0), new Point(x, HeaderHeight));
        }

        // Playhead 수직선 (캐시 A-5)
        context.DrawLine(RenderResourceCache.HeaderPlayheadPen,
            new Point(x, 0), new Point(x, HeaderHeight));

        // 상단 핸들 (삼각형)
        const double handleWidth = 14; // A-1: 약간 축소 (16→14)
        const double handleHeight = 10; // A-1: 축소 (12→10)

        // 핸들 그림자 (캐시 A-5)
        var shadowGeometry = new StreamGeometry();
        using (var ctx = shadowGeometry.Open())
        {
            ctx.BeginFigure(new Point(x + 1, 1), true);
            ctx.LineTo(new Point(x - handleWidth / 2 + 1, handleHeight + 1));
            ctx.LineTo(new Point(x + handleWidth / 2 + 1, handleHeight + 1));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(
            RenderResourceCache.HeaderPlayheadShadowBrush,
            null,
            shadowGeometry);

        // 핸들 본체 (캐시된 그라디언트 A-5)
        var handleGeometry = new StreamGeometry();
        using (var ctx = handleGeometry.Open())
        {
            ctx.BeginFigure(new Point(x, 0), true);
            ctx.LineTo(new Point(x - handleWidth / 2, handleHeight));
            ctx.LineTo(new Point(x + handleWidth / 2, handleHeight));
            ctx.EndFigure(true);
        }
        var handleBorderPen = RenderResourceCache.GetPen(Colors.White, 1.2);
        context.DrawGeometry(
            RenderResourceCache.HeaderPlayheadHandleGradient,
            handleBorderPen,
            handleGeometry);

        // 재생 중 아이콘 (작은 삼각형)
        if (isPlaying)
        {
            var playIconGeometry = new StreamGeometry();
            using (var ctx = playIconGeometry.Open())
            {
                ctx.BeginFigure(new Point(x - 2, 3), true);
                ctx.LineTo(new Point(x + 3, 5.5));
                ctx.LineTo(new Point(x - 2, 8));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(RenderResourceCache.WhiteBrush, null, playIconGeometry);
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

    /// <summary>
    /// 스크럽 시간을 콘텐츠 범위로 클램프 (클립 끝 초과 방지)
    /// 클립이 없으면 그대로 반환. 클립이 있으면 마지막 클립 끝 - 1ms로 제한.
    /// </summary>
    private long ClampTimeToContent(long timeMs)
    {
        if (_viewModel == null || _viewModel.Clips.Count == 0)
            return timeMs;

        long maxEndTime = 0;
        foreach (var clip in _viewModel.Clips)
        {
            var clipEnd = clip.StartTimeMs + clip.DurationMs;
            if (clipEnd > maxEndTime) maxEndTime = clipEnd;
        }

        // 클립 끝 - 1ms까지만 허용 (exclusive end 대응)
        return Math.Min(timeMs, maxEndTime - 1);
    }
}

using Avalonia;
using Avalonia.Media;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 타임라인 렌더링용 브러시/펜/타입페이스 캐시 (GC 압력 최소화)
/// 매 프레임 new SolidColorBrush/Pen 생성 대신 캐시된 불변 객체 재사용
/// </summary>
public static class RenderResourceCache
{
    // ============================================================
    // 정적 브러시 (불변, 앱 수명 동안 재사용)
    // ============================================================

    public static readonly IBrush BackgroundBrush =
        new SolidColorBrush(Color.Parse("#1E1E1E")).ToImmutable();

    public static readonly IBrush TransparentBrush = Brushes.Transparent;
    public static readonly IBrush WhiteBrush = Brushes.White;
    public static readonly IBrush BlackBrush = Brushes.Black;

    // 트랙 라벨
    public static readonly IBrush VideoLabelBrush =
        new SolidColorBrush(Color.FromArgb(200, 100, 180, 255)).ToImmutable();
    public static readonly IBrush AudioLabelBrush =
        new SolidColorBrush(Color.FromArgb(200, 100, 230, 150)).ToImmutable();
    public static readonly IBrush LabelBgBrush =
        new SolidColorBrush(Color.FromArgb(200, 30, 30, 32)).ToImmutable();

    // 클립 공통
    public static readonly IBrush HoverBrush =
        new SolidColorBrush(Color.FromArgb(40, 0, 122, 204)).ToImmutable();
    public static readonly IBrush TrimHandleBrush =
        new SolidColorBrush(Color.FromRgb(255, 200, 80)).ToImmutable();
    public static readonly IBrush DurationTextBrush =
        new SolidColorBrush(Color.FromRgb(255, 220, 80)).ToImmutable();
    public static readonly IBrush DragHighlightBrush =
        new SolidColorBrush(Color.FromArgb(60, 80, 220, 255)).ToImmutable();

    // 링크 연결선
    public static readonly IBrush LinkBrush =
        new SolidColorBrush(Color.FromRgb(80, 220, 255)).ToImmutable();

    // Snap 가이드라인
    public static readonly IBrush SnapYellowBrush =
        new SolidColorBrush(Color.FromRgb(255, 220, 80)).ToImmutable();

    // 뮤트 오버레이
    public static readonly IBrush MuteStripeBrush =
        new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)).ToImmutable();
    public static readonly IBrush MuteOverlayBrush =
        new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)).ToImmutable();
    public static readonly IBrush MuteIconBrush =
        new SolidColorBrush(Color.FromArgb(200, 255, 80, 80)).ToImmutable();
    public static readonly IBrush MuteXBrush =
        new SolidColorBrush(Color.FromRgb(255, 80, 80)).ToImmutable();

    // ============================================================
    // 정적 펜 (불변)
    // ============================================================

    public static readonly IPen TrackBorderPen =
        new Pen(new SolidColorBrush(Color.Parse("#3A3A3C")).ToImmutable(), 0.8);
    public static readonly IPen TrackHighlightPen =
        new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)).ToImmutable(), 1);

    public static readonly IPen SeparatorShadowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)).ToImmutable(), 4);
    public static readonly IPen SeparatorMainPen =
        new Pen(new SolidColorBrush(Color.FromArgb(180, 80, 220, 255)).ToImmutable(), 3);
    public static readonly IPen SeparatorHighlightPen =
        new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)).ToImmutable(), 1);

    public static readonly IPen ClipBorderNormal =
        new Pen(new SolidColorBrush(Color.Parse("#5A5A5C")).ToImmutable(), 1.2);
    public static readonly IPen ClipBorderSelected =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)).ToImmutable(), 2.5);
    public static readonly IPen ClipBorderMinimalSelected =
        new Pen(Brushes.White, 1.5);
    public static readonly IPen ClipBorderMediumSelected =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)).ToImmutable(), 2);
    public static readonly IPen ClipBorderMediumNormal =
        new Pen(new SolidColorBrush(Color.Parse("#5A5A5C")).ToImmutable(), 1);

    public static readonly IPen PlayheadShadowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)).ToImmutable(), 3);
    public static readonly IPen PlayheadBodyPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 50, 50)).ToImmutable(), 2.5);
    public static readonly IPen PlayheadHeadBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)).ToImmutable(), 1.2);

    public static readonly IPen LinkLinePen = CreateLinkPen();
    public static readonly IPen LinkNodeBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)).ToImmutable(), 1);

    public static readonly IPen WaveformCenterPen =
        new Pen(new SolidColorBrush(Color.FromArgb(70, 160, 255, 160)).ToImmutable(), 0.5);

    public static readonly IPen KeyframeShadowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)).ToImmutable(), 2);
    public static readonly IPen KeyframeLinePen =
        new Pen(new SolidColorBrush(Color.FromArgb(180, 80, 220, 255)).ToImmutable(), 1.5);
    public static readonly IPen DiamondBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)).ToImmutable(), 1.5);

    public static readonly IPen SnapShadowPen = CreateDashedPen(
        Color.FromArgb(100, 0, 0, 0), 3, new double[] { 4, 4 });
    public static readonly IPen SnapGlowPen = CreateDashedPen(
        Color.FromArgb(80, 255, 220, 80), 5, new double[] { 4, 4 });
    public static readonly IPen SnapMainPen = CreateDashedPen(
        Color.FromRgb(255, 220, 80), 2, new double[] { 4, 4 });
    public static readonly IPen SnapMagnetPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 220, 80)).ToImmutable(), 2.5);
    public static readonly IPen SnapDeltaBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 220, 80)).ToImmutable(), 1.5);

    public static readonly IPen TooltipBorderPen =
        new Pen(new SolidColorBrush(Color.FromArgb(200, 80, 220, 255)).ToImmutable(), 1.5);
    public static readonly IPen TooltipArrowBorderPen =
        new Pen(new SolidColorBrush(Color.FromArgb(200, 80, 220, 255)).ToImmutable(), 1.5);

    public static readonly IPen TrimHandlePen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 80)).ToImmutable(), 2);

    // 트림 핸들 (그루브 스타일)
    public static readonly IBrush TrimHandleHoverBrush =
        new SolidColorBrush(Color.FromArgb(100, 255, 200, 80)).ToImmutable();
    public static readonly IPen TrimGroovePen =
        new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 240, 180)).ToImmutable(), 1);
    public static readonly IPen TrimHandleOutlinePen =
        new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)).ToImmutable(), 0.5);

    // 서브소눈금 점 + 프레임 번호 (고줌 전용)
    public static readonly IBrush SubMinorTickDotBrush =
        new SolidColorBrush(Color.FromArgb(120, 120, 120, 120)).ToImmutable();
    public static readonly IBrush FrameNumberBrush =
        new SolidColorBrush(Color.FromArgb(150, 180, 180, 180)).ToImmutable();

    // 고스트 아웃라인 (트림 중 원본 범위 표시)
    public static readonly IBrush GhostFillBrush =
        new SolidColorBrush(Color.FromArgb(25, 255, 200, 80)).ToImmutable();
    public static readonly IPen GhostOutlinePen =
        new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 200, 80)).ToImmutable(), 1.5)
        { DashStyle = new DashStyle(new double[] { 6, 4 }, 0) };

    // ============================================================
    // TimelineHeader용 정적 리소스
    // ============================================================

    // 헤더 배경 그라디언트
    public static readonly IBrush HeaderBgGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#3A3A3C"), 0),
            new GradientStop(Color.Parse("#2D2D30"), 0.5),
            new GradientStop(Color.Parse("#252527"), 1)
        }
    }.ToImmutable();

    public static readonly IPen HeaderHighlightPen =
        new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)).ToImmutable(), 1);

    // 눈금 펜
    public static readonly IPen MinorTickPen =
        new Pen(new SolidColorBrush(Color.Parse("#555555")).ToImmutable(), 1);
    public static readonly IPen MajorTickPen =
        new Pen(new SolidColorBrush(Color.Parse("#999999")).ToImmutable(), 1.5);

    // 줌바
    public static readonly IBrush ZoomTrackBrush =
        new SolidColorBrush(Color.FromArgb(180, 40, 40, 42)).ToImmutable();
    public static readonly IPen ZoomTrackBorderPen =
        new Pen(new SolidColorBrush(Color.FromArgb(150, 80, 80, 82)).ToImmutable(), 1);
    public static readonly IBrush ZoomThumbBrush =
        new SolidColorBrush(Color.FromRgb(200, 200, 200)).ToImmutable();
    public static readonly IPen ZoomThumbBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(80, 220, 255)).ToImmutable(), 1.5);
    public static readonly IBrush ZoomGlowBrush =
        new SolidColorBrush(Color.FromArgb(40, 80, 220, 255)).ToImmutable();
    public static readonly IBrush ZoomLabelBrush =
        new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)).ToImmutable();
    public static readonly IBrush ZoomRangeBrush =
        new SolidColorBrush(Color.FromArgb(150, 150, 150, 150)).ToImmutable();

    // 줌바 채움 그라디언트
    public static readonly IBrush ZoomFillGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromRgb(0, 122, 204), 0),
            new GradientStop(Color.FromRgb(28, 151, 234), 0.5),
            new GradientStop(Color.FromRgb(80, 220, 255), 1)
        }
    }.ToImmutable();

    // 상태 정보 배경
    public static readonly IBrush StatusInfoBgBrush =
        new SolidColorBrush(Color.FromArgb(200, 35, 35, 38)).ToImmutable();
    public static readonly IPen StatusInfoBorderPen =
        new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 122, 204)).ToImmutable(), 1);

    // 헤더 Playhead
    public static readonly IPen HeaderPlayheadPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 90, 0)).ToImmutable(), 2);
    public static readonly IBrush HeaderPlayheadShadowBrush =
        new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)).ToImmutable();

    // 헤더 Playhead 글로우 (재생 중 애니메이션)
    public static readonly IPen HeaderPlayheadGlowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 90, 0)).ToImmutable(), 6);
    public static readonly IPen HeaderPlayheadMidGlowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 90, 0)).ToImmutable(), 3);

    // 헤더 Playhead 핸들 그라디언트 (주황색)
    public static readonly IBrush HeaderPlayheadHandleGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromRgb(255, 90, 0), 0),
            new GradientStop(Color.FromRgb(205, 40, 0), 1)
        }
    }.ToImmutable();

    // 시간 라벨 배경 (반투명)
    public static readonly IBrush TimeLabelBgBrush =
        new SolidColorBrush(Color.FromArgb(150, 45, 45, 48)).ToImmutable();

    // 마커 툴팁 배경
    public static readonly IBrush MarkerTooltipBgBrush =
        new SolidColorBrush(Color.FromArgb(220, 40, 40, 40)).ToImmutable();

    // In/Out 포인트
    public static readonly IBrush InPointBrush =
        new SolidColorBrush(Color.FromArgb(220, 92, 184, 92)).ToImmutable();
    public static readonly IBrush OutPointBrush =
        new SolidColorBrush(Color.FromArgb(220, 217, 83, 79)).ToImmutable();
    public static readonly IBrush WorkAreaBrush =
        new SolidColorBrush(Color.FromArgb(30, 0, 122, 204)).ToImmutable();

    // 타입페이스 (SemiBold 추가)
    public static readonly Typeface SegoeUISemiBold =
        new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);

    // ============================================================
    // 정적 타입페이스 (불변)
    // ============================================================

    public static readonly Typeface SegoeUI =
        new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal);
    public static readonly Typeface SegoeUIBold =
        new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
    public static readonly Typeface Consolas =
        new Typeface("Consolas", FontStyle.Normal, FontWeight.Normal);

    // ============================================================
    // 정적 그라디언트 브러시 (자주 사용되는 고정 패턴)
    // ============================================================

    // Playhead 머리 그라디언트
    public static readonly IBrush PlayheadHeadGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromRgb(255, 80, 80), 0),
            new GradientStop(Color.FromRgb(255, 40, 40), 1)
        }
    }.ToImmutable();

    // Snap 임계 영역 그라디언트
    public static readonly IBrush SnapThresholdGradient = new RadialGradientBrush
    {
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromArgb(40, 255, 220, 80), 0),
            new GradientStop(Color.FromArgb(15, 255, 220, 80), 0.5),
            new GradientStop(Color.FromArgb(0, 255, 220, 80), 1)
        }
    }.ToImmutable();

    // ============================================================
    // 동적 브러시 풀 (색상별 캐싱)
    // ============================================================

    private static readonly Dictionary<uint, IBrush> _solidBrushPool = new();
    private static readonly Dictionary<ulong, IBrush> _gradientBrushPool = new();

    /// <summary>
    /// SolidColorBrush 풀에서 조회/생성 (ARGB 키)
    /// </summary>
    public static IBrush GetSolidBrush(Color color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) |
                   ((uint)color.G << 8) | color.B;
        if (!_solidBrushPool.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color).ToImmutable();
            _solidBrushPool[key] = brush;
        }
        return brush;
    }

    /// <summary>
    /// 수직 LinearGradientBrush 풀에서 조회/생성
    /// </summary>
    public static IBrush GetVerticalGradient(Color top, Color bottom)
    {
        uint topKey = ((uint)top.A << 24) | ((uint)top.R << 16) |
                      ((uint)top.G << 8) | top.B;
        uint bottomKey = ((uint)bottom.A << 24) | ((uint)bottom.R << 16) |
                         ((uint)bottom.G << 8) | bottom.B;
        ulong key = ((ulong)topKey << 32) | bottomKey;

        if (!_gradientBrushPool.TryGetValue(key, out var brush))
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(top, 0),
                    new GradientStop(bottom, 1)
                }
            }.ToImmutable();
            _gradientBrushPool[key] = brush;
        }
        return brush;
    }

    /// <summary>
    /// 수평 LinearGradientBrush 풀에서 조회/생성 (좌→우)
    /// </summary>
    private static readonly Dictionary<ulong, IBrush> _hGradientBrushPool = new();

    public static IBrush GetHorizontalGradient(Color left, Color right)
    {
        uint leftKey = ((uint)left.A << 24) | ((uint)left.R << 16) |
                       ((uint)left.G << 8) | left.B;
        uint rightKey = ((uint)right.A << 24) | ((uint)right.R << 16) |
                        ((uint)right.G << 8) | right.B;
        ulong key = ((ulong)leftKey << 32) | rightKey;

        if (!_hGradientBrushPool.TryGetValue(key, out var brush))
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(left, 0),
                    new GradientStop(right, 1)
                }
            }.ToImmutable();
            _hGradientBrushPool[key] = brush;
        }
        return brush;
    }

    /// <summary>
    /// Pen 풀에서 조회/생성 (색상 + 두께 키)
    /// </summary>
    private static readonly Dictionary<(uint, int), IPen> _penPool = new();

    public static IPen GetPen(Color color, double thickness)
    {
        uint colorKey = ((uint)color.A << 24) | ((uint)color.R << 16) |
                        ((uint)color.G << 8) | color.B;
        // 두께를 정수로 양자화 (0.1px 단위)
        int thicknessKey = (int)(thickness * 10);
        var key = (colorKey, thicknessKey);

        if (!_penPool.TryGetValue(key, out var pen))
        {
            pen = new Pen(new SolidColorBrush(color).ToImmutable(), thickness);
            _penPool[key] = pen;
        }
        return pen;
    }

    /// <summary>
    /// 풀 클리어 (테마 변경 시)
    /// </summary>
    public static void ClearPools()
    {
        _solidBrushPool.Clear();
        _gradientBrushPool.Clear();
        _hGradientBrushPool.Clear();
        _penPool.Clear();
    }

    // 툴팁/성능 정보 공통
    public static readonly IBrush TooltipShadowBrush =
        new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)).ToImmutable();
    public static readonly IBrush TooltipBgBrush =
        new SolidColorBrush(Color.FromArgb(250, 40, 40, 42)).ToImmutable();
    public static readonly IBrush SnapDeltaBgBrush =
        new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)).ToImmutable();

    // Playhead 그림자
    public static readonly IBrush PlayheadShadowBrush =
        new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)).ToImmutable();

    // 다이아몬드 키프레임 하이라이트
    public static readonly IPen DiamondHighlightPen =
        new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)).ToImmutable(), 1.2);

    // 툴팁 배경 그라디언트
    public static readonly IBrush TooltipBgGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromArgb(240, 50, 50, 52), 0),
            new GradientStop(Color.FromArgb(250, 40, 40, 42), 1)
        }
    }.ToImmutable();

    // 성능 정보 배경 그라디언트
    public static readonly IBrush PerfInfoBgGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromArgb(200, 30, 30, 32), 0),
            new GradientStop(Color.FromArgb(220, 25, 25, 27), 1)
        }
    }.ToImmutable();

    // ============================================================
    // GraphEditor용 정적 리소스 (Render() 내 매 프레임 생성 방지)
    // ============================================================

    // 배경 그라디언트
    public static readonly IBrush GraphBgGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#252527"), 0),
            new GradientStop(Color.Parse("#1E1E20"), 0.5),
            new GradientStop(Color.Parse("#181A18"), 1)
        }
    }.ToImmutable();

    // 비네팅 효과
    public static readonly IBrush GraphVignetteBrush = new RadialGradientBrush
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
    }.ToImmutable();

    // 그리드 펜
    public static readonly IPen GraphMicroGridPen =
        new Pen(new SolidColorBrush(Color.FromArgb(20, 80, 80, 80)).ToImmutable(), 0.5);
    public static readonly IPen GraphMinorGridPen =
        new Pen(new SolidColorBrush(Color.FromArgb(40, 100, 100, 100)).ToImmutable(), 0.8);
    public static readonly IPen GraphMajorGridPen =
        new Pen(new SolidColorBrush(Color.FromArgb(80, 120, 120, 120)).ToImmutable(), 1.2);

    // X/Y축 펜 (그림자, 본체, 하이라이트)
    public static readonly IPen GraphAxisShadowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)).ToImmutable(), 3);
    public static readonly IPen GraphAxisBodyPen =
        new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)).ToImmutable(), 2.5);
    public static readonly IPen GraphAxisHighlightPen =
        new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)).ToImmutable(), 1);

    // 곡선 펜
    public static readonly IPen GraphCurveShadowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)).ToImmutable(), 3.5);
    public static readonly IPen GraphCurveGlowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(60, 80, 220, 255)).ToImmutable(), 5);
    public static readonly IPen GraphCurveBodyPen =
        new Pen(new SolidColorBrush(Color.FromRgb(80, 220, 255)).ToImmutable(), 2.5);

    // 키프레임 포인트
    public static readonly IBrush GraphKfShadowBrush =
        new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)).ToImmutable();
    public static readonly IBrush GraphKfGlowBrush =
        new SolidColorBrush(Color.FromArgb(80, 255, 220, 80)).ToImmutable();
    public static readonly IBrush GraphKfNormalInnerBrush =
        new SolidColorBrush(Color.FromRgb(255, 255, 255)).ToImmutable();
    public static readonly IBrush GraphKfNormalOuterBrush =
        new SolidColorBrush(Color.FromRgb(200, 200, 200)).ToImmutable();
    public static readonly IBrush GraphKfSelectedInnerBrush =
        new SolidColorBrush(Color.FromRgb(255, 240, 100)).ToImmutable();
    public static readonly IBrush GraphKfSelectedOuterBrush =
        new SolidColorBrush(Color.FromRgb(255, 200, 60)).ToImmutable();
    public static readonly IPen GraphKfNormalBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)).ToImmutable(), 2);
    public static readonly IPen GraphKfSelectedBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 180, 40)).ToImmutable(), 2);

    // 베지어 핸들
    public static readonly IPen GraphHandleLineShadowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)).ToImmutable(), 2.5);
    public static readonly IPen GraphHandleLinePen = CreateDashedPen(
        Color.FromRgb(180, 180, 180), 2, new double[] { 3, 3 });
    public static readonly IBrush GraphHandleOutGradient = new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromRgb(150, 255, 150), 0),
            new GradientStop(Color.FromRgb(80, 200, 80), 1)
        }
    }.ToImmutable();
    public static readonly IBrush GraphHandleInGradient = new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromRgb(255, 150, 150), 0),
            new GradientStop(Color.FromRgb(200, 80, 80), 1)
        }
    }.ToImmutable();
    public static readonly IPen GraphHandleBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)).ToImmutable(), 2);

    // 트랜지션 오버레이 (페이드 인/아웃)
    public static readonly IBrush TransitionFadeInGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromArgb(100, 0, 0, 0), 0),
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
        }
    }.ToImmutable();
    public static readonly IBrush TransitionFadeOutGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
            new GradientStop(Color.FromArgb(100, 0, 0, 0), 1)
        }
    }.ToImmutable();

    // ============================================================
    // 헬퍼
    // ============================================================

    private static IPen CreateLinkPen()
    {
        var pen = new Pen(
            new SolidColorBrush(Color.FromArgb(120, 80, 220, 255)).ToImmutable(), 1.5);
        pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        return pen;
    }

    private static IPen CreateDashedPen(Color color, double thickness, double[] dashPattern)
    {
        var pen = new Pen(new SolidColorBrush(color).ToImmutable(), thickness);
        pen.DashStyle = new DashStyle(dashPattern, 0);
        return pen;
    }
}

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
/// 클립 LOD (줌 레벨별 렌더링 복잡도)
/// </summary>
public enum ClipLOD { Full, Medium, Minimal }

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

    // 썸네일 스트립 서비스
    private ThumbnailStripService? _thumbnailStripService;

    // 오디오 파형 서비스
    private AudioWaveformService? _audioWaveformService;

    // 키프레임 드래그 상태
    private Keyframe? _draggingKeyframe;
    private KeyframeSystem? _draggingKeyframeSystem;
    private ClipModel? _draggingKeyframeClip;
    private bool _isDraggingKeyframe;

    // 성능 모니터링 (FPS)
    private DateTime _lastFrameTime = DateTime.Now;
    private List<double> _frameTimes = new List<double>();
    private double _currentFps = 0;

    // 애니메이션 (선택 펄스 효과)
    private double _selectionPulsePhase = 0;
    private double _glowAccumulatorMs = 0;
    private const double GlowIntervalMs = 100; // 10fps

    // 스냅샷 변경 감지 (트랙 배경 최적화)
    private double _lastRenderedPixelsPerMs = -1;
    private double _lastRenderedScrollOffsetX = -1;
    private int _lastRenderedVideoTrackCount = -1;
    private int _lastRenderedAudioTrackCount = -1;
    private bool _trackBackgroundDirty = true;

    // 재생 헤드 자동 스크롤
    private bool _followPlayhead = true;
    private long _lastPlayheadTimeMs = 0;

    /// <summary>
    /// 가상 스크롤 변경 콜백 (TimelineCanvas에서 설정, header 동기화용)
    /// </summary>
    public Action<double>? OnVirtualScrollChanged { get; set; }

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
        // 이전 구독 해제
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        _snapService = new SnapService(viewModel);

        // IsPlaying 변경 감지 → 렌더링 루프 시작
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.IsPlaying) ||
            e.PropertyName == nameof(TimelineViewModel.CurrentTimeMs))
        {
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
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
        _pixelsPerMs = Math.Clamp(pixelsPerMs, 0.001, 5.0); // 최대 5000%까지 확대
        InvalidateVisual();
    }

    public void SetScrollOffset(double offsetX)
    {
        _scrollOffsetX = offsetX;
        InvalidateVisual();
    }

    public void SetThumbnailService(ThumbnailStripService service)
    {
        _thumbnailStripService = service;
    }

    public void SetAudioWaveformService(AudioWaveformService service)
    {
        _audioWaveformService = service;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // FPS 계산
        var now = DateTime.Now;
        var deltaTime = (now - _lastFrameTime).TotalMilliseconds;
        _lastFrameTime = now;

        if (deltaTime > 0)
        {
            _frameTimes.Add(deltaTime);
            if (_frameTimes.Count > 30) // 최근 30프레임 평균
            {
                _frameTimes.RemoveAt(0);
            }

            var avgDelta = _frameTimes.Average();
            _currentFps = 1000.0 / avgDelta;

            // 선택 펄스 애니메이션 (부드러운 사인 곡선)
            _selectionPulsePhase += deltaTime * 0.002; // 속도 조절
            if (_selectionPulsePhase > Math.PI * 2)
            {
                _selectionPulsePhase -= Math.PI * 2;
            }

            // 선택된 클립 글로우 애니메이션 (10fps 제한 - 유휴 CPU 절약)
            if (_viewModel?.SelectedClips.Count > 0 && !(_viewModel?.IsPlaying ?? false))
            {
                _glowAccumulatorMs += deltaTime;
                if (_glowAccumulatorMs >= GlowIntervalMs)
                {
                    _glowAccumulatorMs = 0;
                    Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Render);
                }
            }

            // 재생 헤드 자동 스크롤 (Playhead Follow) - 드래그/트림 중에는 스킵
            if (_viewModel != null && _followPlayhead && _viewModel.IsPlaying && !_isDragging && !_isTrimming)
            {
                long currentPlayheadTime = _viewModel.CurrentTimeMs;
                if (currentPlayheadTime != _lastPlayheadTimeMs)
                {
                    _lastPlayheadTimeMs = currentPlayheadTime;

                    // Playhead가 화면 밖으로 나가면 가상 스크롤
                    double playheadX = TimeToX(currentPlayheadTime);
                    double viewportWidth = Bounds.Width;

                    // Playhead가 화면 오른쪽 80%를 넘으면 스크롤
                    bool scrollChanged = false;
                    if (playheadX > viewportWidth * 0.8)
                    {
                        _scrollOffsetX += (playheadX - viewportWidth * 0.5);
                        scrollChanged = true;
                    }
                    // Playhead가 화면 왼쪽으로 나가면 스크롤
                    else if (playheadX < viewportWidth * 0.2 && _scrollOffsetX > 0)
                    {
                        _scrollOffsetX -= (viewportWidth * 0.5 - playheadX);
                        _scrollOffsetX = Math.Max(0, _scrollOffsetX);
                        scrollChanged = true;
                    }

                    // TimelineHeader 등 다른 컴포넌트 동기화
                    // CRITICAL: Render() 내에서 다른 Visual의 InvalidateVisual() 호출 금지
                    // → Post로 지연시켜 렌더 패스 완료 후 실행
                    if (scrollChanged)
                    {
                        var offset = _scrollOffsetX;
                        Dispatcher.UIThread.Post(() => OnVirtualScrollChanged?.Invoke(offset),
                            Avalonia.Threading.DispatcherPriority.Render);
                    }
                }

                // 재생 중에는 계속 갱신
                Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Render);
            }
        }

        // 스냅샷 변경 감지 (향후 캐싱 확장 기반)
        bool zoomDirty = Math.Abs(_pixelsPerMs - _lastRenderedPixelsPerMs) > 0.0001;
        bool scrollDirty = Math.Abs(_scrollOffsetX - _lastRenderedScrollOffsetX) > 0.5;
        bool trackLayoutDirty = _videoTracks.Count != _lastRenderedVideoTrackCount
                              || _audioTracks.Count != _lastRenderedAudioTrackCount;
        _trackBackgroundDirty = zoomDirty || scrollDirty || trackLayoutDirty;

        _lastRenderedPixelsPerMs = _pixelsPerMs;
        _lastRenderedScrollOffsetX = _scrollOffsetX;
        _lastRenderedVideoTrackCount = _videoTracks.Count;
        _lastRenderedAudioTrackCount = _audioTracks.Count;

        // 배경
        context.FillRectangle(RenderResourceCache.BackgroundBrush, Bounds);

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

        // 성능 정보 (FPS, 클립 개수 - 우측 하단)
        DrawPerformanceInfo(context);
    }

    private void DrawTrackBackgrounds(DrawingContext context)
    {
        // 비디오 트랙
        for (int i = 0; i < _videoTracks.Count; i++)
        {
            var track = _videoTracks[i];
            double y = i * track.Height;
            var trackRect = new Rect(0, y, Bounds.Width, track.Height);

            // 프로페셔널 그라디언트 배경 (교차 패턴) - 캐시된 브러시 사용
            var isEven = i % 2 == 0;
            var trackGradient = isEven
                ? RenderResourceCache.GetVerticalGradient(Color.Parse("#2D2D30"), Color.Parse("#252527"))
                : RenderResourceCache.GetVerticalGradient(Color.Parse("#252527"), Color.Parse("#1E1E20"));

            context.FillRectangle(trackGradient, trackRect);

            // 미묘한 상단 하이라이트 (3D 효과)
            if (i > 0)
            {
                context.DrawLine(RenderResourceCache.TrackHighlightPen,
                    new Point(0, y),
                    new Point(Bounds.Width, y));
            }

            context.DrawRectangle(RenderResourceCache.TrackBorderPen, trackRect);

            // Lock된 트랙 빗금 오버레이
            if (track.IsLocked)
            {
                DrawLockedTrackOverlay(context, trackRect);
            }
        }

        // 비디오/오디오 트랙 경계 구분선
        double audioStartY = _videoTracks.Sum(t => t.Height);
        if (_videoTracks.Count > 0 && _audioTracks.Count > 0)
        {
            // 구분선: 그림자 → 본체 → 하이라이트
            context.DrawLine(RenderResourceCache.SeparatorShadowPen,
                new Point(0, audioStartY + 2),
                new Point(Bounds.Width, audioStartY + 2));

            context.DrawLine(RenderResourceCache.SeparatorMainPen,
                new Point(0, audioStartY),
                new Point(Bounds.Width, audioStartY));

            context.DrawLine(RenderResourceCache.SeparatorHighlightPen,
                new Point(0, audioStartY - 1),
                new Point(Bounds.Width, audioStartY - 1));

            // 라벨
            var videoLabel = new FormattedText(
                "VIDEO",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUIBold,
                10,
                RenderResourceCache.VideoLabelBrush);

            var audioLabel = new FormattedText(
                "AUDIO",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUIBold,
                10,
                RenderResourceCache.AudioLabelBrush);

            // 라벨 배경
            var videoLabelBg = new Rect(5, audioStartY - 15, videoLabel.Width + 8, 12);
            context.FillRectangle(RenderResourceCache.LabelBgBrush, videoLabelBg);
            context.DrawText(videoLabel, new Point(9, audioStartY - 14));

            var audioLabelBg = new Rect(5, audioStartY + 3, audioLabel.Width + 8, 12);
            context.FillRectangle(RenderResourceCache.LabelBgBrush, audioLabelBg);
            context.DrawText(audioLabel, new Point(9, audioStartY + 4));
        }

        // 오디오 트랙
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            var track = _audioTracks[i];
            double y = audioStartY + i * track.Height;
            var trackRect = new Rect(0, y, Bounds.Width, track.Height);

            // 오디오 트랙 그라디언트 (캐시)
            var isEven = i % 2 == 0;
            var audioTrackGradient = isEven
                ? RenderResourceCache.GetVerticalGradient(Color.Parse("#252828"), Color.Parse("#1E2120"))
                : RenderResourceCache.GetVerticalGradient(Color.Parse("#1E2120"), Color.Parse("#181A18"));

            context.FillRectangle(audioTrackGradient, trackRect);

            // 미묘한 상단 하이라이트
            if (i > 0)
            {
                context.DrawLine(RenderResourceCache.TrackHighlightPen,
                    new Point(0, y),
                    new Point(Bounds.Width, y));
            }

            context.DrawRectangle(RenderResourceCache.TrackBorderPen, trackRect);

            // Lock된 트랙 빗금 오버레이
            if (track.IsLocked)
            {
                DrawLockedTrackOverlay(context, trackRect);
            }
        }
    }

    /// <summary>
    /// Lock된 트랙 배경 빗금 오버레이 (DaVinci Resolve 스타일)
    /// </summary>
    private void DrawLockedTrackOverlay(DrawingContext context, Rect trackRect)
    {
        // 반투명 어두운 오버레이
        context.FillRectangle(
            RenderResourceCache.GetSolidBrush(Color.FromArgb(40, 0, 0, 0)),
            trackRect);

        // 희미한 대각선 빗금 (12px 간격)
        var lockStripePen = RenderResourceCache.GetPen(Color.FromArgb(30, 180, 180, 180), 1);
        for (double sx = trackRect.Left - trackRect.Height; sx < trackRect.Right; sx += 12)
        {
            context.DrawLine(lockStripePen,
                new Point(sx, trackRect.Bottom),
                new Point(sx + trackRect.Height, trackRect.Top));
        }
    }

    private void DrawClips(DrawingContext context)
    {
        if (_clips.Count == 0) return;

        // Viewport 시간 범위 계산 (50px 버퍼 포함 - 클립 경계가 부드럽게 나타나도록)
        long visibleStartMs = XToTime(-50);
        long visibleEndMs = XToTime(Bounds.Width + 50);

        // ViewModel에 Visible Range 전달 (타임라인 전체 기준)
        if (_viewModel != null &&
            (_viewModel.VisibleStartMs != visibleStartMs || _viewModel.VisibleEndMs != visibleEndMs))
        {
            _viewModel.VisibleStartMs = visibleStartMs;
            _viewModel.VisibleEndMs = visibleEndMs;
        }

        // 50개 이상 visible 클립 시 LOD 강제 하향 (성능)
        int visibleClipCount = 0;
        foreach (var clip in _clips)
        {
            long clipEnd = clip.StartTimeMs + clip.DurationMs;
            if (clipEnd >= visibleStartMs && clip.StartTimeMs <= visibleEndMs)
            {
                visibleClipCount++;
            }
        }
        bool forceLowLOD = visibleClipCount > 50;

        int renderedCount = 0;
        foreach (var clip in _clips)
        {
            long clipEndMs = clip.StartTimeMs + clip.DurationMs;
            // viewport 밖 클립 스킵
            if (clipEndMs < visibleStartMs || clip.StartTimeMs > visibleEndMs)
                continue;

            // 썸네일 서비스에 이 클립의 로컬 Visible Range 힌트 전달
            if (_thumbnailStripService != null && clip.DurationMs > 0)
            {
                long localStart = Math.Max(0, visibleStartMs - clip.StartTimeMs);
                long localEnd = Math.Min(clip.DurationMs, visibleEndMs - clip.StartTimeMs);
                if (localEnd > 0 && localStart < clip.DurationMs)
                {
                    _thumbnailStripService.UpdateVisibleRange(clip.FilePath, localStart, localEnd);
                }
            }

            bool isSelected = _viewModel?.SelectedClips.Contains(clip) ?? false;
            bool isHovered = clip == _hoveredClip;
            DrawClip(context, clip, isSelected, isHovered, forceLowLOD);
            renderedCount++;
        }

        if (_clips.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"📊 DrawClips: {renderedCount}/{_clips.Count} clips visible, _pixelsPerMs={_pixelsPerMs}");
        }
    }

    /// <summary>
    /// 클립 픽셀 너비에 따른 LOD 결정
    /// </summary>
    private static ClipLOD GetClipLOD(double clipWidthPx)
    {
        if (clipWidthPx > 80) return ClipLOD.Full;      // 텍스트, 그림자, 아이콘 전부
        if (clipWidthPx > 20) return ClipLOD.Medium;     // 그라디언트 + 이름만
        return ClipLOD.Minimal;                           // 단색 박스만
    }

    private void DrawClip(DrawingContext context, ClipModel clip, bool isSelected, bool isHovered, bool forceLowLOD = false)
    {
        double x = TimeToX(clip.StartTimeMs);
        double width = DurationToWidth(clip.DurationMs);

        // 트랙 Y 위치 계산
        double y = GetTrackYPosition(clip.TrackIndex);
        var track = GetTrackByIndex(clip.TrackIndex);
        if (track == null) return;

        double height = track.Height - 10;
        var clipRect = new Rect(x, y + 5, Math.Max(width, MinClipWidth), height);

        // LOD 결정 (50개 초과 시 Full → Medium 강제 하향)
        var lod = GetClipLOD(clipRect.Width);
        if (forceLowLOD && lod == ClipLOD.Full) lod = ClipLOD.Medium;

        // DisplayMode 오버라이드: Minimal → 항상 Minimal LOD
        var displayMode = track.DisplayMode;
        if (displayMode == ClipDisplayMode.Minimal)
            lod = ClipLOD.Minimal;

        // 드래그 중인 클립 감지
        bool isDragging = _isDragging && clip == _draggingClip;
        bool isTrimming = _isTrimming && clip == _draggingClip;

        // 클립 타입 감지 (비디오/오디오)
        bool isAudioClip = track.Type == TrackType.Audio;

        // 클립 배경 (그라데이션 - DaVinci Resolve 스타일)
        Color topColor, bottomColor;

        if (isAudioClip)
        {
            // 오디오 클립: 초록색 그라데이션
            if (isDragging || isTrimming)
            {
                // 드래그/트림 중: 더 밝고 반투명
                topColor = Color.Parse("#7CD87C");
                bottomColor = Color.Parse("#5CB85C");
            }
            else if (isSelected)
            {
                topColor = Color.Parse("#5CB85C");  // 밝은 초록
                bottomColor = Color.Parse("#449D44");  // 어두운 초록
            }
            else
            {
                topColor = Color.Parse("#3A5A3A");  // 다크 초록
                bottomColor = Color.Parse("#2A4A2A");  // 더 어두운 초록
            }
        }
        else
        {
            // 비디오 클립: 파란색 그라데이션
            if (isDragging || isTrimming)
            {
                // 드래그/트림 중: 더 밝고 반투명
                topColor = Color.Parse("#6AACF2");
                bottomColor = Color.Parse("#4A90E2");
            }
            else if (isSelected)
            {
                topColor = Color.Parse("#4A90E2");  // 밝은 파란색
                bottomColor = Color.Parse("#2D6AA6");  // 어두운 파란색
            }
            else
            {
                topColor = Color.Parse("#3A5A7A");  // 다크 블루
                bottomColor = Color.Parse("#2A4A6A");  // 더 어두운 블루
            }
        }

        // 트랙 뮤트/솔로 상태 확인 및 색상 조정
        bool isTrackMuted = track.IsMuted;
        bool isTrackSolo = _viewModel != null && (
            _videoTracks.Any(t => t.IsSolo && t.Type == TrackType.Video) ||
            _audioTracks.Any(t => t.IsSolo && t.Type == TrackType.Audio));

        // 트랙이 뮤트되었거나, 다른 트랙이 솔로인 경우 어둡게 처리
        bool shouldDimClip = isTrackMuted || (isTrackSolo && !track.IsSolo);

        if (shouldDimClip)
        {
            // 50% 어둡게
            topColor = DarkenColor(topColor, 0.5);
            bottomColor = DarkenColor(bottomColor, 0.5);
        }

        // === LOD: Minimal - 단색 박스만 (가장 빠름) ===
        if (lod == ClipLOD.Minimal)
        {
            context.FillRectangle(RenderResourceCache.GetSolidBrush(topColor), clipRect);
            if (isSelected)
            {
                context.DrawRectangle(RenderResourceCache.ClipBorderMinimalSelected, clipRect);
            }

            // DisplayMode.Minimal: 클립 이름 표시 (LOD Minimal과 달리 이름은 보여줌)
            if (displayMode == ClipDisplayMode.Minimal && width > 30)
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(clip.FilePath);
                if (fileName.Length > 12) fileName = fileName.Substring(0, 9) + "...";
                var minText = new FormattedText(
                    fileName,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    RenderResourceCache.SegoeUIBold,
                    10,
                    RenderResourceCache.WhiteBrush);
                context.DrawText(minText, new Point(x + 4, y + 7));
                context.DrawRectangle(
                    isSelected ? RenderResourceCache.ClipBorderMediumSelected : RenderResourceCache.ClipBorderMediumNormal,
                    clipRect);
            }
            return;
        }

        var gradientBrush = RenderResourceCache.GetVerticalGradient(topColor, bottomColor);

        // === LOD: Medium - 그라디언트 + 이름만 (그림자/아이콘/웨이브폼 생략) ===
        if (lod == ClipLOD.Medium)
        {
            context.FillRectangle(gradientBrush, clipRect);

            // 비디오 클립 썸네일 (Medium LOD에서도 표시)
            if (!isAudioClip && _thumbnailStripService != null && displayMode != ClipDisplayMode.Thumbnail)
            {
                // Filmstrip: 연속 썸네일 (Proxy가 있으면 Proxy 우선 사용)
                var tier = ThumbnailStripService.GetTierForZoom(_pixelsPerMs);
                var previewPath = string.IsNullOrEmpty(clip.ProxyFilePath)
                    ? clip.FilePath
                    : clip.ProxyFilePath;
                var strip = _thumbnailStripService.GetOrRequestStrip(
                    previewPath, clip.DurationMs, tier);
                if (strip?.Thumbnails.Count > 0)
                {
                    DrawThumbnailStrip(context, strip, clipRect, clip);
                }
            }
            else if (!isAudioClip && _thumbnailStripService != null && displayMode == ClipDisplayMode.Thumbnail)
            {
                // Thumbnail: 시작/끝 프레임만
                DrawHeadTailThumbnails(context, clip, clipRect);
            }

            var medBorderPen = isSelected
                ? RenderResourceCache.ClipBorderMediumSelected
                : RenderResourceCache.ClipBorderMediumNormal;
            context.DrawRectangle(medBorderPen, clipRect);

            // 클립 이름만 표시
            if (width > 40)
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(clip.FilePath);
                if (fileName.Length > 15) fileName = fileName.Substring(0, 12) + "...";
                var text = new FormattedText(
                    fileName,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    RenderResourceCache.SegoeUIBold,
                    11,
                    RenderResourceCache.WhiteBrush);
                context.DrawText(text, new Point(x + 4, y + 9));
            }
            return;
        }

        // === LOD: Full - 아래부터 기존 전체 렌더링 ===

        // 클립 그림자 (DaVinci Resolve 스타일)
        var shadowOpacity = (isDragging || isTrimming) ? (byte)120 : (byte)80;
        var shadowOffset = (isDragging || isTrimming) ? 4.0 : 2.0;
        var shadowRect = new Rect(
            clipRect.X + shadowOffset,
            clipRect.Y + shadowOffset,
            clipRect.Width,
            clipRect.Height);
        context.FillRectangle(
            RenderResourceCache.GetSolidBrush(Color.FromArgb(shadowOpacity, 0, 0, 0)),
            shadowRect);

        // 드래그 중 배경 추가 강조
        if (isDragging || isTrimming)
        {
            var dragHighlightRect = new Rect(
                clipRect.X - 2,
                clipRect.Y - 2,
                clipRect.Width + 4,
                clipRect.Height + 4);
            context.FillRectangle(RenderResourceCache.DragHighlightBrush, dragHighlightRect);
        }

        context.FillRectangle(gradientBrush, clipRect);

        // 비디오 클립 + LOD Full/Medium일 때 썸네일 렌더링
        if (!isAudioClip && _thumbnailStripService != null && displayMode != ClipDisplayMode.Thumbnail)
        {
            // Filmstrip: 연속 썸네일 (Proxy가 있으면 Proxy 우선 사용)
            var tier = ThumbnailStripService.GetTierForZoom(_pixelsPerMs);
            var previewPath = string.IsNullOrEmpty(clip.ProxyFilePath)
                ? clip.FilePath
                : clip.ProxyFilePath;
            var strip = _thumbnailStripService.GetOrRequestStrip(
                previewPath, clip.DurationMs, tier);

            if (strip?.Thumbnails.Count > 0)
            {
                DrawThumbnailStrip(context, strip, clipRect, clip);
            }
        }
        else if (!isAudioClip && _thumbnailStripService != null && displayMode == ClipDisplayMode.Thumbnail)
        {
            // Thumbnail: 시작/끝 프레임만
            DrawHeadTailThumbnails(context, clip, clipRect);
        }

        // 색상 라벨 (DaVinci Resolve 스타일 - 클립 상단에 얇은 바)
        if (clip.ColorLabelArgb != 0)
        {
            byte a = (byte)((clip.ColorLabelArgb >> 24) & 0xFF);
            byte r = (byte)((clip.ColorLabelArgb >> 16) & 0xFF);
            byte g = (byte)((clip.ColorLabelArgb >> 8) & 0xFF);
            byte b = (byte)(clip.ColorLabelArgb & 0xFF);

            var colorLabelRect = new Rect(
                clipRect.X,
                clipRect.Y,
                clipRect.Width,
                4); // 4px 높이

            // 그라데이션 색상 라벨
            var labelGradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromArgb(a, r, g, b), 0),
                    new GradientStop(Color.FromArgb((byte)(a * 0.7), r, g, b), 1)
                }
            };

            context.FillRectangle(labelGradient, colorLabelRect);
        }

        // 선택된 클립 펄스 글로우 효과 (애니메이션)
        if (isSelected)
        {
            // 펄스 강도 (0.3 ~ 0.8 사이)
            double pulseIntensity = 0.3 + (Math.Sin(_selectionPulsePhase) * 0.5 + 0.5) * 0.5;

            // 외부 글로우 (큰 반경, 더 약함)
            var glowRect1 = new Rect(
                clipRect.X - 4,
                clipRect.Y - 4,
                clipRect.Width + 8,
                clipRect.Height + 8);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb((byte)(pulseIntensity * 60), 255, 255, 255)),
                glowRect1);

            // 중간 글로우
            var glowRect2 = new Rect(
                clipRect.X - 2,
                clipRect.Y - 2,
                clipRect.Width + 4,
                clipRect.Height + 4);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb((byte)(pulseIntensity * 100), 255, 255, 255)),
                glowRect2);

            // 내부 글로우 (가장 밝음)
            var glowRect3 = new Rect(
                clipRect.X - 1,
                clipRect.Y - 1,
                clipRect.Width + 2,
                clipRect.Height + 2);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb((byte)(pulseIntensity * 150), 80, 220, 255)),
                glowRect3);
        }

        // 호버 효과 (선택되지 않은 클립만)
        if (isHovered && !isSelected)
        {
            var hoverRect = new Rect(
                clipRect.X - 1,
                clipRect.Y - 1,
                clipRect.Width + 2,
                clipRect.Height + 2);
            context.FillRectangle(RenderResourceCache.HoverBrush, hoverRect);
        }

        // 오디오 웨이브폼 (실제 파형 데이터 또는 시뮬레이션)
        if (isAudioClip && width > 50)
        {
            DrawAudioWaveform(context, clipRect, clip);
        }

        // 테두리 (선택된 클립은 밝은 하얀색, 일반은 미묘한 회색)
        context.DrawRectangle(
            isSelected ? RenderResourceCache.ClipBorderSelected : RenderResourceCache.ClipBorderNormal,
            clipRect);

        // 트림 핸들 시각화 (양 끝 10px 영역)
        if (isSelected && width > 30)
        {
            // 왼쪽 트림 핸들
            var leftHandleRect = new Rect(clipRect.X, clipRect.Y, 2, clipRect.Height);
            context.FillRectangle(RenderResourceCache.TrimHandleBrush, leftHandleRect);

            // 오른쪽 트림 핸들
            var rightHandleRect = new Rect(
                clipRect.Right - 2,
                clipRect.Y,
                2,
                clipRect.Height);
            context.FillRectangle(RenderResourceCache.TrimHandleBrush, rightHandleRect);
        }

        // 클립 타입 아이콘 (좌측 상단)
        if (width > 30)
        {
            var iconText = isAudioClip ? "🔊" : "🎬";
            var iconFormatted = new FormattedText(
                iconText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUI,
                14,
                RenderResourceCache.WhiteBrush);

            // 아이콘 배경
            var iconBgRect = new Rect(x + 4, y + 4, 20, 20);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(180, 0, 0, 0)),
                iconBgRect);
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
                RenderResourceCache.SegoeUIBold,
                12,
                RenderResourceCache.WhiteBrush);

            // 텍스트 배경
            var textBgRect = new Rect(x + 28, y + 6, text.Width + 8, text.Height + 6);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(180, 0, 0, 0)),
                textBgRect);

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
                    RenderResourceCache.SegoeUIBold,
                    11,
                    RenderResourceCache.DurationTextBrush);

                var durationX = x + width - durationFormatted.Width - 10;
                var durationBgRect = new Rect(durationX - 4, y + 6, durationFormatted.Width + 8, durationFormatted.Height + 6);
                context.FillRectangle(
                    RenderResourceCache.GetSolidBrush(Color.FromArgb(180, 0, 0, 0)),
                    durationBgRect);
                context.DrawText(durationFormatted, new Point(durationX, y + 9));
            }
        }

        // 클립 전환 효과 오버레이 (페이드 인/아웃 시각화)
        if (width > 30)
        {
            DrawTransitionOverlay(context, clipRect);
        }

        // 뮤트/비활성 클립 오버레이 (줄무늬 패턴)
        if (shouldDimClip)
        {
            // 대각선 줄무늬 패턴
            var stripesPen = RenderResourceCache.GetPen(Color.FromArgb(60, 0, 0, 0), 2);

            for (double stripeX = clipRect.Left; stripeX < clipRect.Right; stripeX += 8)
            {
                context.DrawLine(stripesPen,
                    new Point(stripeX, clipRect.Top),
                    new Point(stripeX - clipRect.Height, clipRect.Bottom));
            }

            // 반투명 검정 오버레이
            context.FillRectangle(RenderResourceCache.MuteOverlayBrush, clipRect);

            // 뮤트 아이콘 (중앙)
            if (width > 60 && height > 30)
            {
                double iconX = clipRect.X + clipRect.Width / 2;
                double iconY = clipRect.Y + clipRect.Height / 2;

                // 스피커 아이콘 with X
                var muteGeometry = new StreamGeometry();
                using (var ctx = muteGeometry.Open())
                {
                    // 스피커 모양
                    ctx.BeginFigure(new Point(iconX - 10, iconY - 6), true);
                    ctx.LineTo(new Point(iconX - 5, iconY - 6));
                    ctx.LineTo(new Point(iconX, iconY - 10));
                    ctx.LineTo(new Point(iconX, iconY + 10));
                    ctx.LineTo(new Point(iconX - 5, iconY + 6));
                    ctx.LineTo(new Point(iconX - 10, iconY + 6));
                    ctx.EndFigure(true);
                }

                context.DrawGeometry(
                    RenderResourceCache.MuteIconBrush,
                    RenderResourceCache.ClipBorderMinimalSelected,
                    muteGeometry);

                // X 표시
                var xPen = RenderResourceCache.GetPen(Color.FromRgb(255, 80, 80), 2.5);
                context.DrawLine(xPen,
                    new Point(iconX + 3, iconY - 8),
                    new Point(iconX + 12, iconY + 8));
                context.DrawLine(xPen,
                    new Point(iconX + 12, iconY - 8),
                    new Point(iconX + 3, iconY + 8));
            }
        }

        // Lock된 트랙 오버레이 (빗금 + 자물쇠 아이콘)
        if (track.IsLocked)
        {
            // 빗금 패턴 (밝은 회색, Mute보다 눈에 띄게)
            var lockStripesPen = RenderResourceCache.GetPen(Color.FromArgb(80, 200, 200, 200), 1);
            for (double stripeX = clipRect.Left; stripeX < clipRect.Right + clipRect.Height; stripeX += 6)
            {
                context.DrawLine(lockStripesPen,
                    new Point(stripeX, clipRect.Top),
                    new Point(stripeX - clipRect.Height, clipRect.Bottom));
            }

            // 반투명 오버레이
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(60, 30, 30, 30)),
                clipRect);

            // 자물쇠 아이콘 (중앙)
            if (width > 40 && height > 25)
            {
                double lockX = clipRect.X + clipRect.Width / 2;
                double lockY = clipRect.Y + clipRect.Height / 2;

                // 자물쇠 몸체 (사각형)
                var bodyRect = new Rect(lockX - 6, lockY - 2, 12, 10);
                context.FillRectangle(
                    RenderResourceCache.GetSolidBrush(Color.FromArgb(200, 0, 122, 204)),
                    bodyRect);

                // 자물쇠 고리 (아치)
                var archPen = RenderResourceCache.GetPen(Color.FromArgb(200, 0, 122, 204), 2);
                context.DrawLine(archPen, new Point(lockX - 4, lockY - 2), new Point(lockX - 4, lockY - 6));
                context.DrawLine(archPen, new Point(lockX + 4, lockY - 2), new Point(lockX + 4, lockY - 6));
                context.DrawLine(archPen, new Point(lockX - 4, lockY - 6), new Point(lockX + 4, lockY - 6));
            }
        }

        // 키프레임 렌더링 (선택된 클립만)
        if (isSelected && _viewModel != null)
        {
            DrawKeyframes(context, clip);
        }
    }

    /// <summary>
    /// Thumbnail 모드: 시작/끝 프레임만 표시 (Premiere Pro 스타일)
    /// </summary>
    private void DrawHeadTailThumbnails(DrawingContext context, ClipModel clip, Rect clipRect)
    {
        if (_thumbnailStripService == null) return;

        var tier = ThumbnailStripService.GetTierForZoom(_pixelsPerMs);
        // Proxy가 있으면 Proxy 우선 사용
        var previewPath = string.IsNullOrEmpty(clip.ProxyFilePath)
            ? clip.FilePath
            : clip.ProxyFilePath;
        var strip = _thumbnailStripService.GetOrRequestStrip(
            previewPath, clip.DurationMs, tier);

        if (strip == null || strip.Thumbnails.Count == 0) return;

        double thumbWidth = clipRect.Height * 1.5; // 16:9 비율 근사
        if (thumbWidth > clipRect.Width / 2) thumbWidth = clipRect.Width / 2;

        // 시작 프레임 (첫 번째 썸네일)
        var firstThumb = strip.Thumbnails[0];
        if (firstThumb?.Bitmap != null)
        {
            var headRect = new Rect(clipRect.X, clipRect.Y, thumbWidth, clipRect.Height);
            using (context.PushClip(headRect))
            {
                context.DrawImage(firstThumb.Bitmap, headRect);
            }
        }

        // 끝 프레임 (마지막 썸네일)
        if (strip.Thumbnails.Count > 1 && clipRect.Width > thumbWidth * 2 + 10)
        {
            var lastThumb = strip.Thumbnails[strip.Thumbnails.Count - 1];
            if (lastThumb?.Bitmap != null)
            {
                var tailRect = new Rect(
                    clipRect.Right - thumbWidth, clipRect.Y,
                    thumbWidth, clipRect.Height);
                using (context.PushClip(tailRect))
                {
                    context.DrawImage(lastThumb.Bitmap, tailRect);
                }
            }
        }
    }

    /// <summary>
    /// 오디오 웨이브폼 렌더링 (DaVinci Resolve 스타일)
    /// 실제 파형 데이터가 있으면 사용, 없으면 시뮬레이션 fallback
    /// </summary>
    private void DrawAudioWaveform(DrawingContext context, Rect clipRect, ClipModel clip)
    {
        var centerY = clipRect.Top + clipRect.Height / 2;

        // 실제 파형 데이터 조회
        WaveformData? waveform = null;
        if (_audioWaveformService != null && !string.IsNullOrEmpty(clip.FilePath))
        {
            waveform = _audioWaveformService.GetOrRequestWaveform(clip.FilePath, clip.DurationMs);
        }

        // WaveformDisplayMode 확인
        var waveformMode = _viewModel?.WaveformMode ?? WaveformDisplayMode.NonRectified;
        if (waveformMode == WaveformDisplayMode.Hidden) return;

        if (waveform != null && waveform.IsComplete && waveform.Peaks.Length > 0)
        {
            // === 실제 파형 렌더링 ===
            DrawRealWaveform(context, clipRect, clip, waveform, centerY, waveformMode);
        }
        else
        {
            // === 시뮬레이션 fallback (데이터 로딩 중) ===
            DrawSimulatedWaveform(context, clipRect, centerY);
        }

        // 중앙선 (가이드라인) - Rectified 모드에서는 하단에 표시
        if (waveformMode == WaveformDisplayMode.Rectified)
        {
            double baseY = clipRect.Bottom - 2;
            context.DrawLine(RenderResourceCache.WaveformCenterPen,
                new Point(clipRect.Left, baseY),
                new Point(clipRect.Right, baseY));
        }
        else
        {
            context.DrawLine(RenderResourceCache.WaveformCenterPen,
                new Point(clipRect.Left, centerY),
                new Point(clipRect.Right, centerY));
        }
    }

    /// <summary>
    /// 실제 오디오 피크 데이터 기반 파형 렌더링
    /// </summary>
    private void DrawRealWaveform(
        DrawingContext context, Rect clipRect, ClipModel clip,
        WaveformData waveform, double centerY, WaveformDisplayMode mode)
    {
        const double MaxAmplitude = 0.42; // 클립 높이의 42%
        double halfHeight = clipRect.Height * MaxAmplitude;

        var waveformPen = RenderResourceCache.GetPen(
            Color.FromArgb(200, 130, 230, 130), 1.4);

        // 피크 인덱스 ↔ 시간 매핑
        double msPerPeak = (double)waveform.SamplesPerPeak / waveform.SampleRate * 1000.0;
        if (msPerPeak <= 0) return;

        // 뷰포트에 보이는 클립 영역만 계산
        double visibleLeft = Math.Max(clipRect.Left, 0);
        double visibleRight = Math.Min(clipRect.Right, Bounds.Width);
        if (visibleRight <= visibleLeft) return;

        double pixelStep = 2.0;

        if (mode == WaveformDisplayMode.Rectified)
        {
            // Rectified: 하단 기준선에서 위로만 그림
            double baseY = clipRect.Bottom - 2;
            double fullHeight = clipRect.Height * 0.85; // 전체 높이의 85% 사용

            for (double x = visibleLeft; x < visibleRight; x += pixelStep)
            {
                double relativeMs = (x - clipRect.Left) / _pixelsPerMs;
                if (relativeMs < 0) continue;

                int peakIndex = (int)(relativeMs / msPerPeak);
                if (peakIndex < 0 || peakIndex >= waveform.Peaks.Length) continue;

                float peak = waveform.Peaks[peakIndex];
                double amplitude = peak * fullHeight;
                if (amplitude < 0.5) continue;

                context.DrawLine(waveformPen,
                    new Point(x, baseY),
                    new Point(x, baseY - amplitude));
            }
        }
        else
        {
            // NonRectified: 중앙 기준 상하 대칭
            for (double x = visibleLeft; x < visibleRight; x += pixelStep)
            {
                double relativeMs = (x - clipRect.Left) / _pixelsPerMs;
                if (relativeMs < 0) continue;

                int peakIndex = (int)(relativeMs / msPerPeak);
                if (peakIndex < 0 || peakIndex >= waveform.Peaks.Length) continue;

                float peak = waveform.Peaks[peakIndex];
                double amplitude = peak * halfHeight;
                if (amplitude < 0.5) continue;

                context.DrawLine(waveformPen,
                    new Point(x, centerY - amplitude),
                    new Point(x, centerY + amplitude));
            }
        }
    }

    /// <summary>
    /// 시뮬레이션 파형 (데이터 로딩 전 표시용)
    /// </summary>
    private void DrawSimulatedWaveform(DrawingContext context, Rect clipRect, double centerY)
    {
        const int SampleInterval = 2;
        const double MaxAmplitude = 0.42;

        var random = new System.Random((int)clipRect.X);
        var waveformPen = RenderResourceCache.GetPen(
            Color.FromArgb(120, 130, 230, 130), 1.4); // 로딩 중은 약간 투명

        for (double x = clipRect.Left; x < clipRect.Right; x += SampleInterval)
        {
            double phase1 = (x - clipRect.Left) / 15.0;
            double phase2 = (x - clipRect.Left) / 35.0;
            double phase3 = (x - clipRect.Left) / 50.0;

            double sine1 = Math.Sin(phase1) * 0.4;
            double sine2 = Math.Sin(phase2) * 0.3;
            double sine3 = Math.Sin(phase3) * 0.2;
            double noise = (random.NextDouble() - 0.5) * 0.6;

            double combinedWave = (sine1 + sine2 + sine3 + noise) / 2.0;
            double amplitude = Math.Abs(combinedWave) * MaxAmplitude * clipRect.Height;

            context.DrawLine(waveformPen,
                new Point(x, centerY - amplitude),
                new Point(x, centerY + amplitude));
        }
    }

    /// <summary>
    /// 클립 내부에 썸네일 스트립 렌더링
    /// 클립 본체 gradient 위에 반투명 썸네일을 배치
    /// </summary>
    private void DrawThumbnailStrip(
        DrawingContext context, ThumbnailStrip strip,
        Rect clipRect, ClipModel clip)
    {
        // 썸네일 표시 영역 (클립 상하 2px 마진)
        double thumbMargin = 2;
        double thumbHeight = clipRect.Height - thumbMargin * 2;
        if (thumbHeight <= 0) return;

        double aspectRatio = 16.0 / 9.0;
        double slotWidth = thumbHeight * aspectRatio; // 각 슬롯 픽셀 폭

        // 현재 재생 위치가 이 클립 내부에 있을 경우, 해당 슬롯을 하이라이트해서
        // "지금 어느 부분이 재생 중인지"를 한눈에 볼 수 있도록 한다.
        bool highlightThisClip = false;
        long currentLocalTimeMs = 0;
        if (_viewModel != null)
        {
            long current = _viewModel.CurrentTimeMs;
            long clipStart = clip.StartTimeMs;
            long clipEnd = clip.StartTimeMs + clip.DurationMs;
            if (current >= clipStart && current <= clipEnd)
            {
                highlightThisClip = true;
                currentLocalTimeMs = current - clipStart; // 클립 로컬 시간
            }
        }

        // 클립 영역으로 클리핑
        using (context.PushClip(clipRect))
        {
            // 연속 타일링: 슬롯을 빈틈없이 배치하고, 각 슬롯에 가장 가까운 썸네일 표시
            double slotX = clipRect.X;
            double clipEndX = clipRect.X + clipRect.Width;
            var thumbList = strip.Thumbnails;
            int thumbCount = thumbList.Count;

            while (slotX < clipEndX && thumbCount > 0)
            {
                // 뷰포트 밖 슬롯 스킵 (성능)
                if (slotX + slotWidth < 0)
                {
                    slotX += slotWidth;
                    continue;
                }
                if (slotX > Bounds.Width)
                    break;

                // 슬롯 중심의 시간 위치 계산 (클립 로컬 시간 기준)
                double slotCenterX = slotX + slotWidth / 2 - clipRect.X;
                long slotTimeMs = (long)(slotCenterX / _pixelsPerMs);

                // 가장 가까운 캐시된 썸네일 찾기 (이진 탐색)
                var bestThumb = FindNearestThumbnail(thumbList, slotTimeMs);

                if (bestThumb != null)
                {
                    // 슬롯 폭이 클립 끝을 넘지 않도록 클램핑
                    double drawWidth = Math.Min(slotWidth, clipEndX - slotX);
                    var destRect = new Rect(
                        slotX,
                        clipRect.Y + thumbMargin,
                        drawWidth,
                        thumbHeight);

                    context.DrawImage(bestThumb.Bitmap, destRect);

                    // 현재 재생 위치가 이 슬롯 근처라면 하이라이트 오버레이
                    if (highlightThisClip)
                    {
                        // 한 슬롯 간격(≈ strip.IntervalMs) 안에 있으면 현재 위치로 간주
                        long interval = Math.Max(strip.IntervalMs, 1);
                        if (Math.Abs(slotTimeMs - currentLocalTimeMs) <= interval / 2)
                        {
                            var highlightBrush = RenderResourceCache.GetSolidBrush(
                                Color.FromArgb(80, 255, 255, 255));
                            context.FillRectangle(highlightBrush, destRect);
                        }
                    }
                }

                slotX += slotWidth;
            }

            // 썸네일 위에 반투명 오버레이 (클립 색상 틴트)
            byte overlayR = 58, overlayG = 123, overlayB = 242;
            if (clip.ColorLabelArgb != 0)
            {
                overlayR = (byte)((clip.ColorLabelArgb >> 16) & 0xFF);
                overlayG = (byte)((clip.ColorLabelArgb >> 8) & 0xFF);
                overlayB = (byte)(clip.ColorLabelArgb & 0xFF);
            }

            var overlayBrush = RenderResourceCache.GetSolidBrush(
                Color.FromArgb(60, overlayR, overlayG, overlayB));
            context.FillRectangle(overlayBrush, clipRect);
        }
    }

    /// <summary>
    /// 이진 탐색으로 특정 시간에 가장 가까운 썸네일 찾기
    /// </summary>
    private static CachedThumbnail? FindNearestThumbnail(List<CachedThumbnail> thumbs, long timeMs)
    {
        if (thumbs.Count == 0) return null;
        if (thumbs.Count == 1) return thumbs[0];

        int lo = 0, hi = thumbs.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (thumbs[mid].SourceTimeMs <= timeMs)
                lo = mid;
            else
                hi = mid;
        }

        // lo와 hi 중 더 가까운 쪽 반환
        long diffLo = Math.Abs(thumbs[lo].SourceTimeMs - timeMs);
        long diffHi = Math.Abs(thumbs[hi].SourceTimeMs - timeMs);
        return diffLo <= diffHi ? thumbs[lo] : thumbs[hi];
    }

    /// <summary>
    /// 클립 전환 효과 오버레이 (페이드 인/아웃 시각화)
    /// </summary>
    private void DrawTransitionOverlay(DrawingContext context, Rect clipRect)
    {
        const double fadeWidth = 15; // 페이드 효과 너비

        // 페이드 인 (좌측)
        var fadeInRect = new Rect(clipRect.X, clipRect.Y, fadeWidth, clipRect.Height);
        var fadeInGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(100, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
            }
        };
        context.FillRectangle(fadeInGradient, fadeInRect);

        // 페이드 인 아이콘 (작은 삼각형)
        var fadeInIconGeometry = new StreamGeometry();
        using (var ctx = fadeInIconGeometry.Open())
        {
            double iconX = clipRect.X + 3;
            double iconY = clipRect.Y + clipRect.Height / 2;
            ctx.BeginFigure(new Point(iconX, iconY - 3), true);
            ctx.LineTo(new Point(iconX + 5, iconY));
            ctx.LineTo(new Point(iconX, iconY + 3));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(
            RenderResourceCache.GetSolidBrush(Color.FromArgb(120, 255, 255, 255)),
            RenderResourceCache.GetPen(Color.FromArgb(180, 255, 255, 255), 0.8),
            fadeInIconGeometry);

        // 페이드 아웃 (우측)
        var fadeOutRect = new Rect(
            clipRect.Right - fadeWidth,
            clipRect.Y,
            fadeWidth,
            clipRect.Height);
        var fadeOutGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(100, 0, 0, 0), 1)
            }
        };
        context.FillRectangle(fadeOutGradient, fadeOutRect);

        // 페이드 아웃 아이콘 (작은 삼각형)
        var fadeOutIconGeometry = new StreamGeometry();
        using (var ctx = fadeOutIconGeometry.Open())
        {
            double iconX = clipRect.Right - 8;
            double iconY = clipRect.Y + clipRect.Height / 2;
            ctx.BeginFigure(new Point(iconX + 5, iconY - 3), true);
            ctx.LineTo(new Point(iconX, iconY));
            ctx.LineTo(new Point(iconX + 5, iconY + 3));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(
            RenderResourceCache.GetSolidBrush(Color.FromArgb(120, 255, 255, 255)),
            RenderResourceCache.GetPen(Color.FromArgb(180, 255, 255, 255), 0.8),
            fadeOutIconGeometry);
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
                context.DrawGeometry(null, RenderResourceCache.KeyframeShadowPen, curveGeometry);

                // 연결선 본체 (밝은 시안색)
                context.DrawGeometry(null, RenderResourceCache.KeyframeLinePen, curveGeometry);
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
        // Viewport 시간 범위 계산
        long visibleStartMs = XToTime(-50);
        long visibleEndMs = XToTime(Bounds.Width + 50);

        // 비디오 클립 중 LinkedAudioClipId가 있는 클립 찾기
        var linkedVideoClips = _clips.Where(c => c.LinkedAudioClipId.HasValue).ToList();

        foreach (var videoClip in linkedVideoClips)
        {
            // viewport 밖 비디오 클립 스킵
            long videoEndMs = videoClip.StartTimeMs + videoClip.DurationMs;
            if (videoEndMs < visibleStartMs || videoClip.StartTimeMs > visibleEndMs)
                continue;

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
            context.DrawLine(RenderResourceCache.LinkLinePen,
                new Point(videoX, videoCenterY),
                new Point(audioX, audioCenterY));

            // 연결 아이콘 (작은 원 - 비디오 클립 쪽)
            var videoIconRect = new Rect(videoX - 4, videoCenterY - 4, 8, 8);
            context.FillRectangle(RenderResourceCache.LinkBrush, videoIconRect);
            context.DrawRectangle(RenderResourceCache.LinkNodeBorderPen, videoIconRect);

            // 연결 아이콘 (작은 원 - 오디오 클립 쪽)
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

        // 재생 중일 때 글로우 효과 (펄스 애니메이션)
        if (_viewModel.IsPlaying)
        {
            double glowIntensity = 0.5 + (Math.Sin(_selectionPulsePhase * 2) * 0.5 + 0.5) * 0.5;

            // 외부 글로우 (더 넓고 약함)
            var outerGlowPen = RenderResourceCache.GetPen(
                Color.FromArgb((byte)(glowIntensity * 100), 255, 80, 80), 8);
            context.DrawLine(outerGlowPen,
                new Point(x, 0),
                new Point(x, Bounds.Height));

            // 중간 글로우
            var midGlowPen = RenderResourceCache.GetPen(
                Color.FromArgb((byte)(glowIntensity * 150), 255, 60, 60), 5);
            context.DrawLine(midGlowPen,
                new Point(x, 0),
                new Point(x, Bounds.Height));
        }

        // Playhead 그림자 (깊이감)
        context.DrawLine(RenderResourceCache.PlayheadShadowPen,
            new Point(x + 1.5, 0),
            new Point(x + 1.5, Bounds.Height));

        // Playhead 본체 (밝은 빨간색)
        context.DrawLine(RenderResourceCache.PlayheadBodyPen,
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
        context.DrawGeometry(RenderResourceCache.PlayheadShadowBrush, null, headShadowGeometry);

        // 헤드 본체 (그라디언트)
        context.DrawGeometry(
            RenderResourceCache.PlayheadHeadGradient,
            RenderResourceCache.PlayheadHeadBorderPen,
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
                RenderResourceCache.SegoeUI,
                fontSize,
                RenderResourceCache.WhiteBrush);
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
        context.FillRectangle(RenderResourceCache.TooltipShadowBrush, shadowRect);

        var bgRect = new Rect(tooltipX, tooltipY, tooltipWidth, tooltipHeight);
        context.FillRectangle(RenderResourceCache.TooltipBgGradient, bgRect);

        // 테두리 (시안색)
        context.DrawRectangle(RenderResourceCache.TooltipBorderPen, bgRect);

        // 텍스트 렌더링
        double textY = tooltipY + padding;
        foreach (var line in tooltipLines)
        {
            var text = new FormattedText(
                line,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUI,
                fontSize,
                RenderResourceCache.WhiteBrush);

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
            RenderResourceCache.TooltipBgBrush,
            RenderResourceCache.TooltipArrowBorderPen,
            arrowGeometry);
    }

    /// <summary>
    /// 성능 정보 표시 (FPS, 클립 개수 - 우측 하단)
    /// </summary>
    private void DrawPerformanceInfo(DrawingContext context)
    {
        const double fontSize = 10;

        var playbackStatus = _viewModel?.IsPlaying == true ? "▶ Playing" : "⏸ Paused";
        var infoLines = new[]
        {
            playbackStatus,
            $"FPS: {_currentFps:F1}",
            $"Clips: {_clips.Count}",
            $"Tracks: {_videoTracks.Count + _audioTracks.Count}"
        };

        const double lineHeight = 14;
        const double padding = 6;

        // 텍스트 크기 계산
        double maxTextWidth = 0;
        foreach (var line in infoLines)
        {
            var text = new FormattedText(
                line,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.Consolas,
                fontSize,
                RenderResourceCache.WhiteBrush);
            maxTextWidth = Math.Max(maxTextWidth, text.Width);
        }

        // 우측 하단 위치
        double infoX = Bounds.Width - maxTextWidth - padding * 2 - 10;
        double infoY = Bounds.Height - (infoLines.Length * lineHeight) - padding * 2 - 10;

        double infoWidth = maxTextWidth + padding * 2;
        double infoHeight = infoLines.Length * lineHeight + padding * 2;

        // 배경 (반투명 그라디언트)
        var bgRect = new Rect(infoX, infoY, infoWidth, infoHeight);
        context.FillRectangle(RenderResourceCache.PerfInfoBgGradient, bgRect);

        // 테두리 (FPS에 따라 색상 변경)
        var borderColor = _currentFps >= 55
            ? Color.FromArgb(150, 100, 255, 100)  // 초록 (높은 FPS)
            : _currentFps >= 30
                ? Color.FromArgb(150, 255, 220, 80)  // 노랑 (보통 FPS)
                : Color.FromArgb(150, 255, 100, 100); // 빨강 (낮은 FPS)

        context.DrawRectangle(RenderResourceCache.GetPen(borderColor, 1.5), bgRect);

        // 텍스트 렌더링
        var textBrush = RenderResourceCache.GetSolidBrush(Color.FromRgb(144, 238, 144));
        double textY = infoY + padding;
        foreach (var line in infoLines)
        {
            var text = new FormattedText(
                line,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.Consolas,
                fontSize,
                textBrush);

            context.DrawText(text, new Point(infoX + padding, textY));
            textY += lineHeight;
        }
    }

    private void DrawSnapGuideline(DrawingContext context, long timeMs)
    {
        double x = TimeToX(timeMs);

        // Snap 임계값 시각화 (양쪽 범위 표시)
        if (_viewModel != null)
        {
            double thresholdX = _viewModel.SnapThresholdMs * _pixelsPerMs;

            // 임계값 범위 (반투명 영역)
            var thresholdRect = new Rect(
                x - thresholdX,
                0,
                thresholdX * 2,
                Bounds.Height);
            context.FillRectangle(RenderResourceCache.SnapThresholdGradient, thresholdRect);
        }

        // Snap 가이드라인 그림자
        context.DrawLine(RenderResourceCache.SnapShadowPen,
            new Point(x + 1.5, 0),
            new Point(x + 1.5, Bounds.Height));

        // Snap 가이드라인 글로우
        context.DrawLine(RenderResourceCache.SnapGlowPen,
            new Point(x, 0),
            new Point(x, Bounds.Height));

        // Snap 가이드라인 본체 (밝은 황금색)
        context.DrawLine(RenderResourceCache.SnapMainPen,
            new Point(x, 0),
            new Point(x, Bounds.Height));

        // 상단 스냅 아이콘 (자석 효과)
        var snapIconGeometry = new StreamGeometry();
        using (var ctx = snapIconGeometry.Open())
        {
            // U자 자석 모양
            ctx.BeginFigure(new Point(x - 8, 10), false);
            ctx.LineTo(new Point(x - 8, 20));
            ctx.QuadraticBezierTo(new Point(x - 8, 25), new Point(x, 25));
            ctx.QuadraticBezierTo(new Point(x + 8, 25), new Point(x + 8, 20));
            ctx.LineTo(new Point(x + 8, 10));
        }
        context.DrawGeometry(null, RenderResourceCache.SnapMagnetPen, snapIconGeometry);

        // 시간 델타 표시 (Snap 위치와 드래그 중인 클립의 시간 차이)
        if (_draggingClip != null && _viewModel != null)
        {
            long dragTime = _draggingClip.StartTimeMs;
            long snapTime = timeMs;
            long deltaMs = snapTime - dragTime;

            // 델타가 0이 아닐 때만 표시
            if (deltaMs != 0)
            {
                string deltaText = deltaMs > 0
                    ? $"+{FormatTime(Math.Abs(deltaMs))}"
                    : $"-{FormatTime(Math.Abs(deltaMs))}";

                var formattedText = new FormattedText(
                    deltaText,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    RenderResourceCache.SegoeUIBold,
                    11,
                    RenderResourceCache.WhiteBrush);

                // 배경 박스 (반투명 검정)
                var textRect = new Rect(
                    x - formattedText.Width / 2 - 6,
                    30,
                    formattedText.Width + 12,
                    formattedText.Height + 6);

                context.FillRectangle(RenderResourceCache.SnapDeltaBgBrush, textRect);

                // 테두리 (황금색)
                context.DrawRectangle(null, RenderResourceCache.SnapDeltaBorderPen, textRect);

                // 텍스트
                context.DrawText(
                    formattedText,
                    new Point(x - formattedText.Width / 2, 33));
            }
        }
    }

    /// <summary>
    /// 시간을 사람이 읽을 수 있는 형식으로 변환 (초.밀리초)
    /// </summary>
    private string FormatTime(long ms)
    {
        double seconds = ms / 1000.0;
        return $"{seconds:F2}s";
    }

    /// <summary>
    /// 색상을 어둡게 만들기
    /// </summary>
    private Color DarkenColor(Color color, double factor)
    {
        return Color.FromArgb(
            color.A,
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor));
    }

    /// <summary>
    /// SMPTE 타임코드 형식으로 변환 (HH:MM:SS:FF)
    /// </summary>
    private string FormatSMPTETimecode(long ms, int fps = 30)
    {
        long totalFrames = (ms * fps) / 1000;
        int frames = (int)(totalFrames % fps);
        int seconds = (int)((totalFrames / fps) % 60);
        int minutes = (int)((totalFrames / (fps * 60)) % 60);
        int hours = (int)(totalFrames / (fps * 3600));

        return $"{hours:D2}:{minutes:D2}:{seconds:D2}:{frames:D2}";
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
                    // Lock된 트랙 클립은 Razor 차단
                    var razorTrack = GetTrackByIndex(clickedClip.TrackIndex);
                    if (razorTrack != null && razorTrack.IsLocked)
                    {
                        e.Handled = true;
                        return;
                    }

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

            // 재생 중이면 즉시 중지 (직접 IsPlaying 설정 + 콜백으로 타이머도 중지)
            if (_viewModel != null && _viewModel.IsPlaying)
            {
                _viewModel.IsPlaying = false;
                _viewModel.RequestStopPlayback?.Invoke();
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

                // Lock된 트랙의 클립은 드래그/트림 차단
                var selectedTrack = GetTrackByIndex(_selectedClip.TrackIndex);
                if (selectedTrack != null && selectedTrack.IsLocked)
                {
                    // 선택만 허용, 드래그/트림 불가
                    Cursor = new Cursor(StandardCursorType.No);
                }
                else
                {
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
            }
            else
            {
                // 빈 공간 클릭: 선택 해제 + Playhead 이동
                if (_viewModel != null)
                {
                    _viewModel.SelectedClips.Clear();
                    // 클릭 위치로 Playhead(CurrentTimeMs) 이동
                    long clickedTimeMs = XToTime(point.X);
                    _viewModel.CurrentTimeMs = Math.Max(0, clickedTimeMs);
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
            // Ctrl + 마우스휠: 수평 Zoom (0.001 ~ 5.0)
            var zoomFactor = e.Delta.Y > 0 ? 1.1 : 0.9;
            var newZoom = Math.Clamp(_pixelsPerMs * zoomFactor, 0.001, 5.0);

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
#pragma warning disable CS0618 // DragEventArgs.Data deprecated in Avalonia 11.3
        if (e.Data.Contains("MediaItem"))
#pragma warning restore CS0618
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
#pragma warning disable CS0618 // DragEventArgs.Data deprecated in Avalonia 11.3
        if (e.Data.Contains("MediaItem") && _viewModel != null)
        {
            var mediaItem = e.Data.Get("MediaItem") as MediaItem;
#pragma warning restore CS0618
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

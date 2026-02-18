using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Linq;
using VortexCut.Core.Models;
using VortexCut.UI.Services;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// ClipCanvasPanel — 렌더링 (트랙 배경, 클립 목록, Playhead, 성능 표시)
/// </summary>
public partial class ClipCanvasPanel
{
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

        // Snap 가이드라인 (드래그 또는 트림 중일 때)
        if ((_isDragging || _isTrimming) && _lastSnappedTimeMs >= 0)
        {
            DrawSnapGuideline(context, _lastSnappedTimeMs);
        }

        // 클립들
        DrawClips(context);

        // 트랜지션 존 오버레이 (겹치는 비디오 클립 쌍 시각화)
        DrawTransitionZones(context);

        // 트림 중 고스트 아웃라인 (원본 범위 표시)
        if (_isTrimming && _draggingClip != null && _draggingClip.SourceDurationMs > 0)
        {
            DrawGhostOutline(context, _draggingClip);
        }

        // 링크된 클립 연결선 (비디오+오디오)
        DrawLinkedClipConnections(context);

        // Playhead
        DrawPlayhead(context);

        // 호버 썸네일 프리뷰
        if (_hoverThumbnailVisible && _hoverThumbnailBitmap != null)
        {
            DrawHoverThumbnailPreview(context);
        }

        // 트림 프리뷰 오버레이
        if (_trimPreviewVisible && _trimPreviewBitmap != null && _isTrimming && _draggingClip != null)
        {
            DrawTrimPreviewOverlay(context, _draggingClip);
        }

        // Swifter 스크럽 썸네일 그리드
        if (_scrubGridVisible && _isScrubbing)
        {
            DrawScrubGrid(context);
        }

        // 성능 정보 (FPS, 클립 개수 - 우측 하단)
        DrawPerformanceInfo(context);
    }

    private void DrawTrackBackgrounds(DrawingContext context)
    {
        // 트랙 순서: V1 → 자막(S1) → V2~V6 → A1~A4
        double currentY = 0;
        int v1 = V1Count;

        // ─── V1 ───
        for (int i = 0; i < v1; i++)
        {
            var track = _videoTracks[i];
            var trackRect = new Rect(0, currentY, Bounds.Width, track.Height);
            context.FillRectangle(
                RenderResourceCache.GetVerticalGradient(Color.Parse("#2D2D30"), Color.Parse("#252527")),
                trackRect);
            context.DrawRectangle(RenderResourceCache.TrackBorderPen, trackRect);
            if (track.IsLocked) DrawLockedTrackOverlay(context, trackRect);
            if (track.IsArmed)
                context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(220, 230, 126, 34)),
                    new Rect(0, currentY, 3, track.Height));
            currentY += track.Height;
        }

        // ─── 자막 트랙 (V1 바로 아래) ───
        if (_subtitleTracks.Count > 0)
        {
            // V1-자막 얇은 구분선
            if (v1 > 0)
                context.DrawLine(RenderResourceCache.SeparatorMainPen,
                    new Point(0, currentY), new Point(Bounds.Width, currentY));

            for (int i = 0; i < _subtitleTracks.Count; i++)
            {
                var track = _subtitleTracks[i];
                var trackRect = new Rect(0, currentY, Bounds.Width, track.Height);
                var subGradient = i % 2 == 0
                    ? RenderResourceCache.GetVerticalGradient(Color.Parse("#2D2820"), Color.Parse("#252018"))
                    : RenderResourceCache.GetVerticalGradient(Color.Parse("#252018"), Color.Parse("#1E1A12"));
                context.FillRectangle(subGradient, trackRect);
                if (i > 0)
                    context.DrawLine(RenderResourceCache.TrackHighlightPen,
                        new Point(0, currentY), new Point(Bounds.Width, currentY));
                context.DrawRectangle(RenderResourceCache.TrackBorderPen, trackRect);
                if (track.IsLocked) DrawLockedTrackOverlay(context, trackRect);
                currentY += track.Height;
            }

            // 자막-V2 구분선
            if (_videoTracks.Count > v1)
                context.DrawLine(RenderResourceCache.SeparatorMainPen,
                    new Point(0, currentY), new Point(Bounds.Width, currentY));
        }

        // ─── V2~V6 ───
        for (int i = v1; i < _videoTracks.Count; i++)
        {
            var track = _videoTracks[i];
            var trackRect = new Rect(0, currentY, Bounds.Width, track.Height);
            var isEven = i % 2 == 0;
            context.FillRectangle(isEven
                ? RenderResourceCache.GetVerticalGradient(Color.Parse("#2D2D30"), Color.Parse("#252527"))
                : RenderResourceCache.GetVerticalGradient(Color.Parse("#252527"), Color.Parse("#1E1E20")),
                trackRect);
            // 자막 구분선 이후 첫 번째 V2 트랙은 상단 하이라이트 없음 (구분선이 이미 있음)
            if (_subtitleTracks.Count == 0 && i > 0)
                context.DrawLine(RenderResourceCache.TrackHighlightPen,
                    new Point(0, currentY), new Point(Bounds.Width, currentY));
            else if (_subtitleTracks.Count > 0 && i > v1)
                context.DrawLine(RenderResourceCache.TrackHighlightPen,
                    new Point(0, currentY), new Point(Bounds.Width, currentY));
            context.DrawRectangle(RenderResourceCache.TrackBorderPen, trackRect);
            if (track.IsLocked) DrawLockedTrackOverlay(context, trackRect);
            if (track.IsArmed)
                context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(220, 230, 126, 34)),
                    new Rect(0, currentY, 3, track.Height));
            currentY += track.Height;
        }

        // ─── VIDEO/AUDIO 구분선 ───
        double audioStartY = currentY;
        if (_videoTracks.Count > 0 && _audioTracks.Count > 0)
        {
            context.DrawLine(RenderResourceCache.SeparatorShadowPen,
                new Point(0, audioStartY + 2), new Point(Bounds.Width, audioStartY + 2));
            context.DrawLine(RenderResourceCache.SeparatorMainPen,
                new Point(0, audioStartY), new Point(Bounds.Width, audioStartY));
            context.DrawLine(RenderResourceCache.SeparatorHighlightPen,
                new Point(0, audioStartY - 1), new Point(Bounds.Width, audioStartY - 1));

            var videoLabel = new FormattedText("VIDEO", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, RenderResourceCache.SegoeUIBold, 10, RenderResourceCache.VideoLabelBrush);
            var audioLabel = new FormattedText("AUDIO", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, RenderResourceCache.SegoeUIBold, 10, RenderResourceCache.AudioLabelBrush);
            context.FillRectangle(RenderResourceCache.LabelBgBrush, new Rect(5, audioStartY - 15, videoLabel.Width + 8, 12));
            context.DrawText(videoLabel, new Point(9, audioStartY - 14));
            context.FillRectangle(RenderResourceCache.LabelBgBrush, new Rect(5, audioStartY + 3, audioLabel.Width + 8, 12));
            context.DrawText(audioLabel, new Point(9, audioStartY + 4));
        }

        // ─── 오디오 트랙 ───
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            var track = _audioTracks[i];
            var trackRect = new Rect(0, currentY, Bounds.Width, track.Height);
            context.FillRectangle(i % 2 == 0
                ? RenderResourceCache.GetVerticalGradient(Color.Parse("#252828"), Color.Parse("#1E2120"))
                : RenderResourceCache.GetVerticalGradient(Color.Parse("#1E2120"), Color.Parse("#181A18")),
                trackRect);
            if (i > 0)
                context.DrawLine(RenderResourceCache.TrackHighlightPen,
                    new Point(0, currentY), new Point(Bounds.Width, currentY));
            context.DrawRectangle(RenderResourceCache.TrackBorderPen, trackRect);
            if (track.IsLocked) DrawLockedTrackOverlay(context, trackRect);
            if (track.IsArmed)
                context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(220, 230, 126, 34)),
                    new Rect(0, currentY, 3, track.Height));
            currentY += track.Height;
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
    /// 호버 썸네일 프리뷰 렌더링 (클립 위 160x90 팝업)
    /// </summary>
    private void DrawHoverThumbnailPreview(DrawingContext context)
    {
        if (_hoverThumbnailBitmap == null) return;

        const double thumbWidth = 160;
        const double thumbHeight = 90;
        const double padding = 4;
        const double labelHeight = 18;
        const double shadowOffset = 3;

        // 팝업 위치: 마우스 위 + 약간 위로
        double popupWidth = thumbWidth + padding * 2;
        double popupHeight = thumbHeight + labelHeight + padding * 2;
        double popupX = _hoverThumbnailPos.X - popupWidth / 2;
        double popupY = _hoverThumbnailPos.Y - popupHeight - 12;

        // 화면 밖으로 나가지 않도록 클램프
        popupX = Math.Clamp(popupX, 2, Bounds.Width - popupWidth - 2);
        popupY = Math.Max(2, popupY);

        var popupRect = new Rect(popupX, popupY, popupWidth, popupHeight);

        // 그림자
        var shadowRect = new Rect(popupX + shadowOffset, popupY + shadowOffset, popupWidth, popupHeight);
        context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(80, 0, 0, 0)), shadowRect);

        // 배경
        context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(240, 30, 30, 35)), popupRect);

        // 썸네일 이미지
        var imageRect = new Rect(popupX + padding, popupY + padding, thumbWidth, thumbHeight);
        context.DrawImage(_hoverThumbnailBitmap, imageRect);

        // 테두리
        context.DrawRectangle(null, RenderResourceCache.GetPen(Color.FromArgb(180, 100, 100, 110), 1), popupRect);

        // 시간 라벨
        string timeLabel = FormatSMPTETimecode(_hoverThumbnailTimeMs);
        var formattedTime = new FormattedText(
            timeLabel,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            RenderResourceCache.Consolas,
            10,
            RenderResourceCache.WhiteBrush);

        context.DrawText(formattedTime,
            new Point(popupX + (popupWidth - formattedTime.Width) / 2,
                       popupY + padding + thumbHeight + 2));
    }

    /// <summary>
    /// Swifter 스크럽 썸네일 그리드 (4x2)
    /// </summary>
    private void DrawScrubGrid(DrawingContext context)
    {
        const double cellWidth = 120;
        const double cellHeight = 68;
        const double cellPadding = 3;
        const double labelHeight = 14;
        const int cols = 4;
        const int rows = 2;
        const double gridPadding = 6;

        double gridWidth = cols * (cellWidth + cellPadding) - cellPadding + gridPadding * 2;
        double gridHeight = rows * (cellHeight + labelHeight + cellPadding) - cellPadding + gridPadding * 2;

        // 그리드 위치: 화면 상단 중앙
        double gridX = (Bounds.Width - gridWidth) / 2;
        double gridY = Math.Max(4, _scrubGridY - gridHeight - 16);

        // 배경
        var bgRect = new Rect(gridX, gridY, gridWidth, gridHeight);
        context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(220, 20, 20, 25)), bgRect);
        context.DrawRectangle(null, RenderResourceCache.GetPen(Color.FromArgb(150, 80, 80, 90), 1), bgRect);

        // 셀 렌더링
        for (int i = 0; i < 8; i++)
        {
            int col = i % cols;
            int row = i / cols;

            double cellX = gridX + gridPadding + col * (cellWidth + cellPadding);
            double cellY = gridY + gridPadding + row * (cellHeight + labelHeight + cellPadding);

            // 썸네일
            var imageRect = new Rect(cellX, cellY, cellWidth, cellHeight);
            if (_scrubGridBitmaps[i] != null)
            {
                context.DrawImage(_scrubGridBitmaps[i]!, imageRect);
            }
            else
            {
                context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(60, 40, 40, 40)), imageRect);
            }

            // 현재 위치 하이라이트 (인덱스 3 = 현재)
            if (i == 3)
            {
                context.DrawRectangle(null, RenderResourceCache.GetPen(Color.FromArgb(220, 255, 200, 80), 2), imageRect);
            }
            else
            {
                context.DrawRectangle(null, RenderResourceCache.GetPen(Color.FromArgb(80, 100, 100, 110), 0.5), imageRect);
            }

            // 시간 라벨
            long timeMs = _scrubGridTimeMs[i];
            string timeLabel = FormatTime(Math.Max(0, timeMs));
            var formattedTime = new FormattedText(
                timeLabel,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.Consolas,
                8,
                i == 3 ? RenderResourceCache.GetSolidBrush(Color.FromArgb(255, 255, 200, 80))
                       : RenderResourceCache.GetSolidBrush(Color.FromArgb(160, 180, 180, 180)));

            context.DrawText(formattedTime,
                new Point(cellX + (cellWidth - formattedTime.Width) / 2, cellY + cellHeight + 1));
        }
    }

    /// <summary>
    /// 트림 중 에지 프레임 프리뷰 오버레이 (클립 위 160x90)
    /// </summary>
    private void DrawTrimPreviewOverlay(DrawingContext context, ClipModel clip)
    {
        if (_trimPreviewBitmap == null) return;

        const double thumbWidth = 160;
        const double thumbHeight = 90;
        const double padding = 4;
        const double labelHeight = 18;
        const double shadowOffset = 3;

        // 프리뷰 위치: 트림 에지 위 중앙
        double edgeX = _trimEdge == ClipEdge.Left
            ? TimeToX(clip.StartTimeMs)
            : TimeToX(clip.StartTimeMs + clip.DurationMs);

        double trackY = GetTrackYPosition(clip.TrackIndex);

        double popupWidth = thumbWidth + padding * 2;
        double popupHeight = thumbHeight + labelHeight + padding * 2;
        double popupX = edgeX - popupWidth / 2;
        double popupY = trackY - popupHeight - 8;

        // 화면 밖 보정
        popupX = Math.Clamp(popupX, 2, Bounds.Width - popupWidth - 2);
        popupY = Math.Max(2, popupY);

        var popupRect = new Rect(popupX, popupY, popupWidth, popupHeight);

        // 그림자
        var shadowRect = new Rect(popupX + shadowOffset, popupY + shadowOffset, popupWidth, popupHeight);
        context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(80, 0, 0, 0)), shadowRect);

        // 배경 (주황 틴트)
        context.FillRectangle(RenderResourceCache.GetSolidBrush(Color.FromArgb(240, 40, 32, 25)), popupRect);

        // 썸네일 이미지
        var imageRect = new Rect(popupX + padding, popupY + padding, thumbWidth, thumbHeight);
        context.DrawImage(_trimPreviewBitmap, imageRect);

        // 테두리 (주황)
        context.DrawRectangle(null, RenderResourceCache.GetPen(Color.FromArgb(200, 230, 126, 34), 1.5), popupRect);

        // 시간 라벨
        string edgeLabel = _trimEdge == ClipEdge.Left ? "IN" : "OUT";
        string timeLabel = $"{edgeLabel}: {FormatSMPTETimecode(_trimPreviewTimeMs)}";
        var formattedTime = new FormattedText(
            timeLabel,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            RenderResourceCache.Consolas,
            10,
            RenderResourceCache.WhiteBrush);

        context.DrawText(formattedTime,
            new Point(popupX + (popupWidth - formattedTime.Width) / 2,
                       popupY + padding + thumbHeight + 2));
    }

    /// <summary>
    /// 트림 중 원본 소스 범위를 점선 아웃라인으로 표시 (고스트 아웃라인)
    /// 사용자가 얼마나 더 확장 가능한지 시각적으로 보여줌
    /// </summary>
    private void DrawGhostOutline(DrawingContext context, ClipModel clip)
    {
        var track = GetTrackByIndex(clip.TrackIndex);
        if (track == null) return;

        double trackY = GetTrackYPosition(clip.TrackIndex);
        double trackHeight = track.Height;

        // 현재 클립 위치
        double clipX = TimeToX(clip.StartTimeMs);
        double clipWidth = DurationToWidth(clip.DurationMs);

        // 원본 전체 범위 계산
        // 원본 시작: 현재 StartTimeMs에서 TrimStartMs만큼 뒤로
        long originalStartMs = clip.StartTimeMs - clip.TrimStartMs;
        // 원본 끝: 원본 시작 + 전체 소스 길이
        long originalEndMs = originalStartMs + clip.SourceDurationMs;

        double originalX = TimeToX(originalStartMs);
        double originalEndX = TimeToX(originalEndMs);
        double originalWidth = originalEndX - originalX;

        // 원본 범위가 현재 클립과 같으면 표시 안함
        if (clip.TrimStartMs <= 0 && clip.SourceDurationMs <= clip.DurationMs + clip.TrimStartMs)
            return;

        const double margin = 2;

        // 왼쪽 확장 가능 영역 (TrimStartMs > 0이면)
        if (clip.TrimStartMs > 0)
        {
            double leftGhostX = originalX;
            double leftGhostWidth = clipX - originalX;
            if (leftGhostWidth > 1)
            {
                var leftRect = new Rect(leftGhostX, trackY + margin, leftGhostWidth, trackHeight - margin * 2);
                context.FillRectangle(RenderResourceCache.GhostFillBrush, leftRect);
                context.DrawRectangle(null, RenderResourceCache.GhostOutlinePen, leftRect);

                // 확장 가능 시간 표시
                long leftExtentMs = clip.TrimStartMs;
                if (leftGhostWidth > 40)
                {
                    var timeText = new FormattedText(
                        $"-{FormatTime(leftExtentMs)}",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        RenderResourceCache.SegoeUI,
                        9,
                        RenderResourceCache.GetSolidBrush(Color.FromArgb(160, 255, 200, 80)));
                    context.DrawText(timeText,
                        new Point(leftGhostX + (leftGhostWidth - timeText.Width) / 2,
                                  trackY + (trackHeight - timeText.Height) / 2));
                }
            }
        }

        // 오른쪽 확장 가능 영역
        long rightExtentMs = clip.SourceDurationMs - clip.TrimStartMs - clip.DurationMs;
        if (rightExtentMs > 0)
        {
            double rightGhostX = clipX + clipWidth;
            double rightGhostWidth = DurationToWidth(rightExtentMs);
            if (rightGhostWidth > 1)
            {
                var rightRect = new Rect(rightGhostX, trackY + margin, rightGhostWidth, trackHeight - margin * 2);
                context.FillRectangle(RenderResourceCache.GhostFillBrush, rightRect);
                context.DrawRectangle(null, RenderResourceCache.GhostOutlinePen, rightRect);

                // 확장 가능 시간 표시
                if (rightGhostWidth > 40)
                {
                    var timeText = new FormattedText(
                        $"+{FormatTime(rightExtentMs)}",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        RenderResourceCache.SegoeUI,
                        9,
                        RenderResourceCache.GetSolidBrush(Color.FromArgb(160, 255, 200, 80)));
                    context.DrawText(timeText,
                        new Point(rightGhostX + (rightGhostWidth - timeText.Width) / 2,
                                  trackY + (trackHeight - timeText.Height) / 2));
                }
            }
        }
    }
}

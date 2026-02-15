using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

        // 오디오/자막 트랙 경계 구분선
        if (_subtitleTracks.Count > 0)
        {
            double subtitleStartY = audioStartY + _audioTracks.Sum(t => t.Height);

            // 구분선
            context.DrawLine(RenderResourceCache.SeparatorMainPen,
                new Point(0, subtitleStartY),
                new Point(Bounds.Width, subtitleStartY));

            // SUBTITLE 라벨
            var subtitleLabel = new FormattedText(
                "SUBTITLE",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUIBold,
                10,
                RenderResourceCache.GetSolidBrush(Color.Parse("#FFC857")));

            var subtitleLabelBg = new Rect(5, subtitleStartY + 3, subtitleLabel.Width + 8, 12);
            context.FillRectangle(RenderResourceCache.LabelBgBrush, subtitleLabelBg);
            context.DrawText(subtitleLabel, new Point(9, subtitleStartY + 4));

            // 자막 트랙
            for (int i = 0; i < _subtitleTracks.Count; i++)
            {
                var track = _subtitleTracks[i];
                double y = subtitleStartY + i * track.Height;
                var trackRect = new Rect(0, y, Bounds.Width, track.Height);

                // 자막 트랙 그라디언트 (앰버/골드 톤)
                var subIsEven = i % 2 == 0;
                var subtitleTrackGradient = subIsEven
                    ? RenderResourceCache.GetVerticalGradient(Color.Parse("#2D2820"), Color.Parse("#252018"))
                    : RenderResourceCache.GetVerticalGradient(Color.Parse("#252018"), Color.Parse("#1E1A12"));

                context.FillRectangle(subtitleTrackGradient, trackRect);

                if (i > 0)
                {
                    context.DrawLine(RenderResourceCache.TrackHighlightPen,
                        new Point(0, y), new Point(Bounds.Width, y));
                }

                context.DrawRectangle(RenderResourceCache.TrackBorderPen, trackRect);

                if (track.IsLocked)
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
}

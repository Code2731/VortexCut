using Avalonia;
using Avalonia.Media;
using System.Linq;
using VortexCut.Core.Models;
using VortexCut.UI.Services;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// ClipCanvasPanel — 개별 클립 렌더링 (DrawClip, 썸네일, 웨이브폼, 트랜지션 오버레이)
/// </summary>
public partial class ClipCanvasPanel
{
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

        // 클립 타입 감지 (비디오/오디오/자막)
        bool isAudioClip = track.Type == TrackType.Audio;
        bool isSubtitleClip = track.Type == TrackType.Subtitle;

        // 클립 배경 (그라데이션 - DaVinci Resolve 스타일)
        Color topColor, bottomColor;

        if (isSubtitleClip)
        {
            // 자막 클립: 앰버/골드 그라데이션
            if (isDragging || isTrimming)
            {
                topColor = Color.Parse("#FFD87C");
                bottomColor = Color.Parse("#FFC857");
            }
            else if (isSelected)
            {
                topColor = Color.Parse("#FFC857");
                bottomColor = Color.Parse("#E0A830");
            }
            else
            {
                topColor = Color.Parse("#7A6A3A");
                bottomColor = Color.Parse("#6A5A2A");
            }
        }
        else if (isAudioClip)
        {
            // 오디오 클립: 초록색 그라데이션
            if (isDragging || isTrimming)
            {
                topColor = Color.Parse("#7CD87C");
                bottomColor = Color.Parse("#5CB85C");
            }
            else if (isSelected)
            {
                topColor = Color.Parse("#5CB85C");
                bottomColor = Color.Parse("#449D44");
            }
            else
            {
                topColor = Color.Parse("#3A5A3A");
                bottomColor = Color.Parse("#2A4A2A");
            }
        }
        else
        {
            // 비디오 클립: 파란색 그라데이션
            if (isDragging || isTrimming)
            {
                topColor = Color.Parse("#6AACF2");
                bottomColor = Color.Parse("#4A90E2");
            }
            else if (isSelected)
            {
                topColor = Color.Parse("#4A90E2");
                bottomColor = Color.Parse("#2D6AA6");
            }
            else
            {
                topColor = Color.Parse("#3A5A7A");
                bottomColor = Color.Parse("#2A4A6A");
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
                4);

            // 캐시된 브러시 풀 사용 (매 클립마다 LinearGradientBrush 생성 방지)
            var labelColor = Color.FromArgb(a, r, g, b);
            var labelFadeColor = Color.FromArgb((byte)(a * 0.7), r, g, b);
            var labelBrush = RenderResourceCache.GetHorizontalGradient(labelColor, labelFadeColor);

            context.FillRectangle(labelBrush, colorLabelRect);
        }

        // 선택된 클립 펄스 글로우 효과 (애니메이션)
        if (isSelected)
        {
            double pulseIntensity = 0.3 + (Math.Sin(_selectionPulsePhase) * 0.5 + 0.5) * 0.5;

            var glowRect1 = new Rect(
                clipRect.X - 4, clipRect.Y - 4,
                clipRect.Width + 8, clipRect.Height + 8);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb((byte)(pulseIntensity * 60), 255, 255, 255)),
                glowRect1);

            var glowRect2 = new Rect(
                clipRect.X - 2, clipRect.Y - 2,
                clipRect.Width + 4, clipRect.Height + 4);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb((byte)(pulseIntensity * 100), 255, 255, 255)),
                glowRect2);

            var glowRect3 = new Rect(
                clipRect.X - 1, clipRect.Y - 1,
                clipRect.Width + 2, clipRect.Height + 2);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb((byte)(pulseIntensity * 150), 80, 220, 255)),
                glowRect3);
        }

        // 호버 효과 (선택되지 않은 클립만)
        if (isHovered && !isSelected)
        {
            var hoverRect = new Rect(
                clipRect.X - 1, clipRect.Y - 1,
                clipRect.Width + 2, clipRect.Height + 2);
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

        // 트림 핸들 (그루브 스타일 — img.ly 참고)
        if ((isSelected || (isHovered && _hoveredEdge != ClipEdge.None)) && width > 30)
        {
            DrawTrimHandle(context, clipRect, ClipEdge.Left,
                isHovered && _hoveredEdge == ClipEdge.Left);
            DrawTrimHandle(context, clipRect, ClipEdge.Right,
                isHovered && _hoveredEdge == ClipEdge.Right);
        }

        // 클립 타입 아이콘 (좌측 상단)
        if (width > 30)
        {
            var iconText = isSubtitleClip ? "T" : (isAudioClip ? "🔊" : "🎬");
            var iconFormatted = new FormattedText(
                iconText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                isSubtitleClip ? RenderResourceCache.SegoeUIBold : RenderResourceCache.SegoeUI,
                isSubtitleClip ? 12 : 14,
                RenderResourceCache.WhiteBrush);

            var iconBgRect = new Rect(x + 4, y + 4, 20, 20);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(180, 0, 0, 0)),
                iconBgRect);
            context.DrawText(iconFormatted, new Point(x + 7, y + 5));
        }

        // 클립 이름 또는 자막 텍스트 (가독성 개선)
        if (width > 40)
        {
            string displayName;
            if (isSubtitleClip && clip is SubtitleClipModel subtitleClip)
            {
                displayName = subtitleClip.Text.Replace('\n', ' ');
            }
            else
            {
                displayName = System.IO.Path.GetFileNameWithoutExtension(clip.FilePath);
            }
            if (displayName.Length > 20)
                displayName = displayName.Substring(0, 17) + "...";

            var text = new FormattedText(
                displayName,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUIBold,
                12,
                RenderResourceCache.WhiteBrush);

            var textBgRect = new Rect(x + 28, y + 6, text.Width + 8, text.Height + 6);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(180, 0, 0, 0)),
                textBgRect);

            context.DrawText(text, new Point(x + 32, y + 9));

            // 클립 지속시간 표시 (우측 상단)
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

        // 이펙트 뱃지 (우측 하단, Full LOD + 80px 이상)
        if (width > 80 && !isSubtitleClip)
        {
            DrawEffectBadges(context, clip, clipRect);
        }

        // 클립 전환 효과 오버레이 (페이드 인/아웃 시각화)
        if (width > 30)
        {
            DrawTransitionOverlay(context, clipRect);
        }

        // 뮤트/비활성 클립 오버레이 (줄무늬 패턴)
        if (shouldDimClip)
        {
            var stripesPen = RenderResourceCache.GetPen(Color.FromArgb(60, 0, 0, 0), 2);

            for (double stripeX = clipRect.Left; stripeX < clipRect.Right; stripeX += 8)
            {
                context.DrawLine(stripesPen,
                    new Point(stripeX, clipRect.Top),
                    new Point(stripeX - clipRect.Height, clipRect.Bottom));
            }

            context.FillRectangle(RenderResourceCache.MuteOverlayBrush, clipRect);

            // 뮤트 아이콘 (중앙)
            if (width > 60 && height > 30)
            {
                double iconX = clipRect.X + clipRect.Width / 2;
                double iconY = clipRect.Y + clipRect.Height / 2;

                var muteGeometry = new StreamGeometry();
                using (var ctx = muteGeometry.Open())
                {
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
            var lockStripesPen = RenderResourceCache.GetPen(Color.FromArgb(80, 200, 200, 200), 1);
            for (double stripeX = clipRect.Left; stripeX < clipRect.Right + clipRect.Height; stripeX += 6)
            {
                context.DrawLine(lockStripesPen,
                    new Point(stripeX, clipRect.Top),
                    new Point(stripeX - clipRect.Height, clipRect.Bottom));
            }

            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(60, 30, 30, 30)),
                clipRect);

            if (width > 40 && height > 25)
            {
                double lockX = clipRect.X + clipRect.Width / 2;
                double lockY = clipRect.Y + clipRect.Height / 2;

                var bodyRect = new Rect(lockX - 6, lockY - 2, 12, 10);
                context.FillRectangle(
                    RenderResourceCache.GetSolidBrush(Color.FromArgb(200, 0, 122, 204)),
                    bodyRect);

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
        var previewPath = string.IsNullOrEmpty(clip.ProxyFilePath)
            ? clip.FilePath
            : clip.ProxyFilePath;
        var strip = _thumbnailStripService.GetOrRequestStrip(
            previewPath, clip.DurationMs, tier);

        if (strip == null || strip.Thumbnails.Count == 0) return;

        double thumbWidth = clipRect.Height * 1.5;
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
    /// </summary>
    private void DrawAudioWaveform(DrawingContext context, Rect clipRect, ClipModel clip)
    {
        var centerY = clipRect.Top + clipRect.Height / 2;

        WaveformData? waveform = null;
        if (_audioWaveformService != null && !string.IsNullOrEmpty(clip.FilePath))
        {
            waveform = _audioWaveformService.GetOrRequestWaveform(clip.FilePath, clip.DurationMs);
        }

        var waveformMode = _viewModel?.WaveformMode ?? WaveformDisplayMode.NonRectified;
        if (waveformMode == WaveformDisplayMode.Hidden) return;

        if (waveform != null && waveform.IsComplete && waveform.Peaks.Length > 0)
        {
            DrawRealWaveform(context, clipRect, clip, waveform, centerY, waveformMode);
        }
        else
        {
            DrawSimulatedWaveform(context, clipRect, centerY);
        }

        // 중앙선 (가이드라인)
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
        const double MaxAmplitude = 0.42;
        double halfHeight = clipRect.Height * MaxAmplitude;

        var waveformPen = RenderResourceCache.GetPen(
            Color.FromArgb(200, 130, 230, 130), 1.4);

        double msPerPeak = (double)waveform.SamplesPerPeak / waveform.SampleRate * 1000.0;
        if (msPerPeak <= 0) return;

        double visibleLeft = Math.Max(clipRect.Left, 0);
        double visibleRight = Math.Min(clipRect.Right, Bounds.Width);
        if (visibleRight <= visibleLeft) return;

        double pixelStep = 2.0;

        if (mode == WaveformDisplayMode.Rectified)
        {
            double baseY = clipRect.Bottom - 2;
            double fullHeight = clipRect.Height * 0.85;

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
            Color.FromArgb(120, 130, 230, 130), 1.4);

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
    /// </summary>
    private void DrawThumbnailStrip(
        DrawingContext context, ThumbnailStrip strip,
        Rect clipRect, ClipModel clip)
    {
        double thumbMargin = 2;
        double thumbHeight = clipRect.Height - thumbMargin * 2;
        if (thumbHeight <= 0) return;

        double aspectRatio = 16.0 / 9.0;
        double slotWidth = thumbHeight * aspectRatio;

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
                currentLocalTimeMs = current - clipStart;
            }
        }

        using (context.PushClip(clipRect))
        {
            double slotX = clipRect.X;
            double clipEndX = clipRect.X + clipRect.Width;
            var thumbList = strip.Thumbnails;
            int thumbCount = thumbList.Count;

            while (slotX < clipEndX && thumbCount > 0)
            {
                if (slotX + slotWidth < 0)
                {
                    slotX += slotWidth;
                    continue;
                }
                if (slotX > Bounds.Width)
                    break;

                double slotCenterX = slotX + slotWidth / 2 - clipRect.X;
                long slotTimeMs = (long)(slotCenterX / _pixelsPerMs);

                var bestThumb = FindNearestThumbnail(thumbList, slotTimeMs);

                if (bestThumb != null)
                {
                    double drawWidth = Math.Min(slotWidth, clipEndX - slotX);
                    var destRect = new Rect(
                        slotX,
                        clipRect.Y + thumbMargin,
                        drawWidth,
                        thumbHeight);

                    context.DrawImage(bestThumb.Bitmap, destRect);

                    if (highlightThisClip)
                    {
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

        long diffLo = Math.Abs(thumbs[lo].SourceTimeMs - timeMs);
        long diffHi = Math.Abs(thumbs[hi].SourceTimeMs - timeMs);
        return diffLo <= diffHi ? thumbs[lo] : thumbs[hi];
    }

    /// <summary>
    /// 클립 전환 효과 오버레이 (페이드 인/아웃 시각화)
    /// </summary>
    private void DrawTransitionOverlay(DrawingContext context, Rect clipRect)
    {
        const double fadeWidth = 15;

        // 페이드 인 (좌측)
        var fadeInRect = new Rect(clipRect.X, clipRect.Y, fadeWidth, clipRect.Height);
        context.FillRectangle(RenderResourceCache.TransitionFadeInGradient, fadeInRect);

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
        context.FillRectangle(RenderResourceCache.TransitionFadeOutGradient, fadeOutRect);

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

    /// <summary>
    /// 클립 이펙트 뱃지 렌더링 (우측 하단에 C/S/F/T 표시)
    /// </summary>
    private void DrawEffectBadges(DrawingContext context, ClipModel clip, Rect clipRect)
    {
        var badges = new List<(string label, Color color)>();

        // Color (색보정)
        if (clip.Brightness != 0 || clip.Contrast != 0 || clip.Saturation != 0 || clip.Temperature != 0)
            badges.Add(("C", Color.FromRgb(255, 165, 0))); // Orange

        // Speed (속도)
        if (Math.Abs(clip.Speed - 1.0) > 0.01)
            badges.Add(("S", Color.FromRgb(0, 200, 255))); // Cyan

        // Fade (페이드)
        if (clip.FadeInMs > 0 || clip.FadeOutMs > 0)
            badges.Add(("F", Color.FromRgb(180, 120, 255))); // Purple

        // Transition (전환)
        if (clip.TransitionType != TransitionType.None)
            badges.Add(("T", Color.FromRgb(100, 255, 100))); // Green

        if (badges.Count == 0) return;

        double badgeSize = 14;
        double spacing = 2;
        double totalWidth = badges.Count * badgeSize + (badges.Count - 1) * spacing;
        double startX = clipRect.Right - totalWidth - 6;
        double badgeY = clipRect.Bottom - badgeSize - 4;

        for (int i = 0; i < badges.Count; i++)
        {
            var (label, color) = badges[i];
            double bx = startX + i * (badgeSize + spacing);

            // 배경 원
            var bgRect = new Rect(bx, badgeY, badgeSize, badgeSize);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(200, 0, 0, 0)),
                bgRect);
            context.FillRectangle(
                RenderResourceCache.GetSolidBrush(Color.FromArgb(180, color.R, color.G, color.B)),
                bgRect);

            // 글자
            var text = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RenderResourceCache.SegoeUIBold,
                9,
                RenderResourceCache.WhiteBrush);

            context.DrawText(text, new Point(bx + (badgeSize - text.Width) / 2, badgeY + (badgeSize - text.Height) / 2));
        }
    }

    /// <summary>
    /// 트림 핸들 렌더링 (그루브 스타일 — 3개 수직 줄 무늬)
    /// </summary>
    private void DrawTrimHandle(DrawingContext context, Rect clipRect, ClipEdge edge, bool isHighlighted)
    {
        const double handleWidth = 8;
        double x = (edge == ClipEdge.Left) ? clipRect.X : clipRect.Right - handleWidth;
        double y = clipRect.Y;
        double h = clipRect.Height;

        var handleRect = new Rect(x, y, handleWidth, h);

        // 호버 시 배경 하이라이트
        if (isHighlighted)
        {
            context.FillRectangle(RenderResourceCache.TrimHandleHoverBrush, handleRect);
        }

        // 외곽 바 (트림 핸들 기본 색상)
        var barRect = new Rect(
            edge == ClipEdge.Left ? clipRect.X : clipRect.Right - 2,
            y, 2, h);
        context.FillRectangle(RenderResourceCache.TrimHandleBrush, barRect);

        // 그루브 라인 (3개 수직 줄)
        double centerX = x + handleWidth / 2;
        double grooveY1 = y + h * 0.3;
        double grooveY2 = y + h * 0.7;
        for (int i = -1; i <= 1; i++)
        {
            double gx = centerX + i * 2.5;
            context.DrawLine(RenderResourceCache.TrimGroovePen,
                new Point(gx, grooveY1), new Point(gx, grooveY2));
        }
    }
}

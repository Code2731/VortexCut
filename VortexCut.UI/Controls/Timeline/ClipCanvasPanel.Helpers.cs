using Avalonia;
using Avalonia.Media;
using VortexCut.Core.Models;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// ClipCanvasPanel — 좌표 변환, 트랙 유틸리티, 포맷 헬퍼
/// </summary>
public partial class ClipCanvasPanel
{
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

    // 트랙 순서: V1 → 자막(S1) → V2~V6 → A1~A4
    // 자막을 V1 바로 아래에 배치하여 영상-자막 편집 편의성 향상
    private int V1Count => _videoTracks.Count > 0 ? 1 : 0;

    private double GetTrackYPosition(int trackIndex)
    {
        double y = 0;
        int idx = 0;
        int v1 = V1Count;

        // V1
        for (int i = 0; i < v1; i++)
        {
            if (idx == trackIndex) return y;
            y += _videoTracks[i].Height;
            idx++;
        }

        // 자막 트랙 (V1 바로 아래)
        for (int i = 0; i < _subtitleTracks.Count; i++)
        {
            if (idx == trackIndex) return y;
            y += _subtitleTracks[i].Height;
            idx++;
        }

        // V2~V6
        for (int i = v1; i < _videoTracks.Count; i++)
        {
            if (idx == trackIndex) return y;
            y += _videoTracks[i].Height;
            idx++;
        }

        // 오디오 트랙
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            if (idx == trackIndex) return y;
            y += _audioTracks[i].Height;
            idx++;
        }

        return y;
    }

    private TrackModel? GetTrackByIndex(int index)
    {
        int v1 = V1Count;
        int idx = 0;

        // V1
        if (v1 > 0 && index == idx) return _videoTracks[0];
        idx += v1;

        // 자막 트랙
        if (index >= idx && index < idx + _subtitleTracks.Count)
            return _subtitleTracks[index - idx];
        idx += _subtitleTracks.Count;

        // V2~V6
        int v2Count = _videoTracks.Count - v1;
        if (index >= idx && index < idx + v2Count)
            return _videoTracks[v1 + (index - idx)];
        idx += v2Count;

        // 오디오 트랙
        if (index >= idx && index < idx + _audioTracks.Count)
            return _audioTracks[index - idx];

        return null;
    }

    private int GetTrackIndexAtY(double y)
    {
        double currentY = 0;
        int idx = 0;
        int v1 = V1Count;

        // V1
        for (int i = 0; i < v1; i++)
        {
            if (y >= currentY && y < currentY + _videoTracks[i].Height) return idx;
            currentY += _videoTracks[i].Height;
            idx++;
        }

        // 자막 트랙
        for (int i = 0; i < _subtitleTracks.Count; i++)
        {
            if (y >= currentY && y < currentY + _subtitleTracks[i].Height) return idx;
            currentY += _subtitleTracks[i].Height;
            idx++;
        }

        // V2~V6
        for (int i = v1; i < _videoTracks.Count; i++)
        {
            if (y >= currentY && y < currentY + _videoTracks[i].Height) return idx;
            currentY += _videoTracks[i].Height;
            idx++;
        }

        // 오디오 트랙
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            if (y >= currentY && y < currentY + _audioTracks[i].Height) return idx;
            currentY += _audioTracks[i].Height;
            idx++;
        }

        return 0;
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
}

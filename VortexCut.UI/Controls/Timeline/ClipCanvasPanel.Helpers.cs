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

    private double GetTrackYPosition(int trackIndex)
    {
        double y = 0;
        int idx = 0;

        // 비디오 트랙
        for (int i = 0; i < _videoTracks.Count; i++)
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

        // 자막 트랙
        for (int i = 0; i < _subtitleTracks.Count; i++)
        {
            if (idx == trackIndex) return y;
            y += _subtitleTracks[i].Height;
            idx++;
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

        int subtitleIndex = index - _videoTracks.Count - _audioTracks.Count;
        if (subtitleIndex >= 0 && subtitleIndex < _subtitleTracks.Count)
            return _subtitleTracks[subtitleIndex];

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

        // 자막 트랙 검사
        for (int i = 0; i < _subtitleTracks.Count; i++)
        {
            if (y >= currentY && y < currentY + _subtitleTracks[i].Height)
                return _videoTracks.Count + _audioTracks.Count + i;
            currentY += _subtitleTracks[i].Height;
        }

        return 0; // 기본값
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

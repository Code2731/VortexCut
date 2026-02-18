using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.ObjectModel;
using VortexCut.Core.Models;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 트랙 헤더 목록 패널 (왼쪽 60px 고정폭)
/// </summary>
public class TrackListPanel : StackPanel
{
    private ObservableCollection<TrackModel>? _videoTracks;
    private ObservableCollection<TrackModel>? _audioTracks;
    private ObservableCollection<TrackModel>? _subtitleTracks;

    public TrackListPanel()
    {
        Orientation = Avalonia.Layout.Orientation.Vertical;
        Width = 60;
        ClipToBounds = true;
    }

    public void SetTracks(
        ObservableCollection<TrackModel> videoTracks,
        ObservableCollection<TrackModel> audioTracks,
        ObservableCollection<TrackModel>? subtitleTracks = null)
    {
        _videoTracks = videoTracks;
        _audioTracks = audioTracks;
        _subtitleTracks = subtitleTracks;

        RebuildHeaders();

        // 트랙 변경 감지
        videoTracks.CollectionChanged += (s, e) => RebuildHeaders();
        audioTracks.CollectionChanged += (s, e) => RebuildHeaders();
        if (subtitleTracks != null)
            subtitleTracks.CollectionChanged += (s, e) => RebuildHeaders();
    }

    /// <summary>
    /// ScrollViewer 수직 스크롤 동기화 — ClipCanvasPanel이 스크롤되면 같이 이동
    /// </summary>
    public void SetVerticalOffset(double offsetY)
    {
        RenderTransform = new TranslateTransform(0, -offsetY);
    }

    private void RebuildHeaders()
    {
        // 헤더 순서: V1 → 자막(S1) → V2~V6 → A1~A4
        Children.Clear();

        // V1
        if (_videoTracks != null && _videoTracks.Count > 0)
            Children.Add(new TrackHeaderControl { Track = _videoTracks[0], Height = _videoTracks[0].Height });

        // 자막 트랙 (V1 바로 아래)
        if (_subtitleTracks != null)
            foreach (var track in _subtitleTracks)
                Children.Add(new TrackHeaderControl { Track = track, Height = track.Height });

        // V2~V6
        if (_videoTracks != null)
            for (int i = 1; i < _videoTracks.Count; i++)
                Children.Add(new TrackHeaderControl { Track = _videoTracks[i], Height = _videoTracks[i].Height });

        // 오디오 트랙
        if (_audioTracks != null)
            foreach (var track in _audioTracks)
                Children.Add(new TrackHeaderControl { Track = track, Height = track.Height });
    }
}

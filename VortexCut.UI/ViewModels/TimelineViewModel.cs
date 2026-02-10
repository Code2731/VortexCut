using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Models;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// 타임라인 ViewModel
/// </summary>
public partial class TimelineViewModel : ViewModelBase
{
    private readonly ProjectService _projectService;
    private ulong _nextTrackId = 1;

    [ObservableProperty]
    private ObservableCollection<ClipModel> _clips = new();

    [ObservableProperty]
    private ObservableCollection<TrackModel> _videoTracks = new();

    [ObservableProperty]
    private ObservableCollection<TrackModel> _audioTracks = new();

    [ObservableProperty]
    private long _currentTimeMs = 0;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private ClipModel? _selectedClip;

    [ObservableProperty]
    private ObservableCollection<ClipModel> _selectedClips = new();

    // Snap 설정
    [ObservableProperty]
    private bool _snapEnabled = true;

    [ObservableProperty]
    private long _snapThresholdMs = 100;

    // Razor 모드
    [ObservableProperty]
    private bool _razorModeEnabled = false;

    public RazorTool? RazorTool { get; private set; }

    public TimelineViewModel(ProjectService projectService)
    {
        _projectService = projectService;
        InitializeDefaultTracks();
        RazorTool = new RazorTool(this);
    }

    /// <summary>
    /// 기본 트랙 초기화 (8개 비디오 + 4개 오디오)
    /// </summary>
    private void InitializeDefaultTracks()
    {
        // 8개 비디오 트랙
        for (int i = 0; i < 8; i++)
        {
            AddVideoTrack();
        }

        // 4개 오디오 트랙
        for (int i = 0; i < 4; i++)
        {
            AddAudioTrack();
        }
    }

    /// <summary>
    /// 비디오 파일 추가
    /// </summary>
    public async Task AddVideoClipAsync(string filePath)
    {
        await Task.Run(() =>
        {
            // TODO: 실제 비디오 길이 가져오기 (FFmpeg 메타데이터)
            long durationMs = 5000; // 임시로 5초
            var clip = _projectService.AddVideoClip(filePath, CurrentTimeMs, durationMs);

            // UI 스레드에서 실행
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Clips.Add(clip);
            });
        });
    }

    /// <summary>
    /// MediaItem으로부터 클립 추가 (드래그앤드롭용)
    /// </summary>
    public void AddClipFromMediaItem(MediaItem mediaItem, long startTimeMs, int trackIndex)
    {
        var clip = _projectService.AddVideoClip(mediaItem.FilePath, startTimeMs, mediaItem.DurationMs, trackIndex);
        Clips.Add(clip);
    }

    /// <summary>
    /// 타임라인 초기화
    /// </summary>
    public void Reset()
    {
        Clips.Clear();
        CurrentTimeMs = 0;
        SelectedClip = null;
    }

    [RelayCommand]
    private void SelectClip(ClipModel clip)
    {
        SelectedClip = clip;
    }

    [RelayCommand]
    private void DeleteSelectedClip()
    {
        if (SelectedClip != null)
        {
            Clips.Remove(SelectedClip);
            SelectedClip = null;
        }
    }

    /// <summary>
    /// 비디오 트랙 추가
    /// </summary>
    [RelayCommand]
    public void AddVideoTrack()
    {
        // TODO: ProjectService.AddVideoTrack() 연동
        var track = new TrackModel
        {
            Id = _nextTrackId++,
            Index = VideoTracks.Count,
            Type = TrackType.Video,
            Name = $"V{VideoTracks.Count + 1}",
            ColorArgb = 0xFF0000FF // Blue
        };
        VideoTracks.Add(track);
    }

    /// <summary>
    /// 오디오 트랙 추가
    /// </summary>
    [RelayCommand]
    public void AddAudioTrack()
    {
        // TODO: ProjectService.AddAudioTrack() 연동
        var track = new TrackModel
        {
            Id = _nextTrackId++,
            Index = AudioTracks.Count,
            Type = TrackType.Audio,
            Name = $"A{AudioTracks.Count + 1}",
            ColorArgb = 0xFF00FF00 // Green
        };
        AudioTracks.Add(track);
    }

    /// <summary>
    /// 트랙 제거
    /// </summary>
    public void RemoveTrack(TrackModel track)
    {
        if (track.Type == TrackType.Video)
        {
            VideoTracks.Remove(track);
            // 인덱스 재정렬
            for (int i = 0; i < VideoTracks.Count; i++)
            {
                VideoTracks[i].Index = i;
            }
        }
        else
        {
            AudioTracks.Remove(track);
            for (int i = 0; i < AudioTracks.Count; i++)
            {
                AudioTracks[i].Index = i;
            }
        }
    }
}

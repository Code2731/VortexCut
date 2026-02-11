using Avalonia.Controls;
using VortexCut.Core.Models;
using VortexCut.UI.Controls.Timeline;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Controls;

/// <summary>
/// Kdenlive 스타일 타임라인 캔버스 (Grid 컨테이너)
/// </summary>
public class TimelineCanvas : Grid
{
    private readonly TimelineMinimap _minimap;
    private readonly TimelineHeader _timelineHeader;
    private readonly TrackListPanel _trackListPanel;
    private readonly ClipCanvasPanel _clipCanvasPanel;
    private readonly ScrollViewer _scrollViewer;

    private TimelineViewModel? _viewModel;
    private double _pixelsPerMs = 0.1;
    private double _scrollOffsetX = 0;

    // 이벤트 핸들러 저장 (구독 해제용)
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _videoTracksChangedHandler;
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _audioTracksChangedHandler;
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _clipsChangedHandler;

    /// <summary>
    /// ViewModel 참조
    /// </summary>
    public TimelineViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            // 이전 ViewModel 구독 해제 (메모리 누수 방지)
            if (_viewModel != null)
            {
                if (_videoTracksChangedHandler != null)
                    _viewModel.VideoTracks.CollectionChanged -= _videoTracksChangedHandler;
                if (_audioTracksChangedHandler != null)
                    _viewModel.AudioTracks.CollectionChanged -= _audioTracksChangedHandler;
                if (_clipsChangedHandler != null)
                    _viewModel.Clips.CollectionChanged -= _clipsChangedHandler;
            }

            _viewModel = value;

            if (_viewModel != null)
            {
                _minimap.SetViewModel(_viewModel);
                _timelineHeader.SetViewModel(_viewModel);
                _clipCanvasPanel.SetViewModel(_viewModel);
                _trackListPanel.SetTracks(_viewModel.VideoTracks, _viewModel.AudioTracks);
                _clipCanvasPanel.SetTracks(_viewModel.VideoTracks.ToList(), _viewModel.AudioTracks.ToList()); // CRITICAL: 트랙 초기 설정!

                // 트랙/클립 변경 감지 (핸들러 저장)
                _videoTracksChangedHandler = (s, e) => UpdateTracks();
                _audioTracksChangedHandler = (s, e) => UpdateTracks();
                _clipsChangedHandler = (s, e) => UpdateClips();

                _viewModel.VideoTracks.CollectionChanged += _videoTracksChangedHandler;
                _viewModel.AudioTracks.CollectionChanged += _audioTracksChangedHandler;
                _viewModel.Clips.CollectionChanged += _clipsChangedHandler;
            }
        }
    }

    public TimelineCanvas()
    {
        // Grid 레이아웃 설정
        RowDefinitions = new RowDefinitions("20,60,*"); // Minimap 20px, Header 60px, 나머지는 트랙
        ColumnDefinitions = new ColumnDefinitions("60,*"); // 트랙 헤더 60px, 나머지는 클립

        // 컴포넌트 생성
        _minimap = new TimelineMinimap();
        _timelineHeader = new TimelineHeader();
        _trackListPanel = new TrackListPanel();
        _clipCanvasPanel = new ClipCanvasPanel();

        // 가상 스크롤 변경 시 TimelineHeader 동기화
        _clipCanvasPanel.OnVirtualScrollChanged = (offsetX) =>
        {
            _scrollOffsetX = offsetX;
            _timelineHeader.SetScrollOffset(offsetX);
        };

        // ScrollViewer (수평/수직 스크롤)
        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _clipCanvasPanel
        };

        // Grid 배치
        // Row 0: Minimap (전체 컬럼 병합)
        _minimap.SetValue(ColumnSpanProperty, 2);
        _minimap.SetValue(RowProperty, 0);

        // Row 1: TimelineHeader (전체 컬럼 병합)
        _timelineHeader.SetValue(ColumnSpanProperty, 2);
        _timelineHeader.SetValue(RowProperty, 1);

        // Row 2, Column 0: TrackListPanel
        _trackListPanel.SetValue(RowProperty, 2);
        _trackListPanel.SetValue(ColumnProperty, 0);

        // Row 2, Column 1: ClipCanvasPanel (ScrollViewer로 감싸기)
        _scrollViewer.SetValue(RowProperty, 2);
        _scrollViewer.SetValue(ColumnProperty, 1);

        Children.Add(_minimap);
        Children.Add(_timelineHeader);
        Children.Add(_trackListPanel);
        Children.Add(_scrollViewer);

        // 스크롤 이벤트 연결
        _scrollViewer.ScrollChanged += OnScrollChanged;
    }

    /// <summary>
    /// Zoom 레벨 설정 (Ctrl + 마우스휠)
    /// </summary>
    public void SetZoom(double pixelsPerMs)
    {
        _pixelsPerMs = Math.Clamp(pixelsPerMs, 0.01, 5.0); // 최대 500%까지 확대
        _timelineHeader.SetZoom(_pixelsPerMs);
        _clipCanvasPanel.SetZoom(_pixelsPerMs);
    }

    /// <summary>
    /// 스크롤 변경 이벤트
    /// </summary>
    private void OnScrollChanged(object? sender, Avalonia.Controls.ScrollChangedEventArgs e)
    {
        if (_scrollViewer != null)
        {
            _scrollOffsetX = _scrollViewer.Offset.X;
            _timelineHeader.SetScrollOffset(_scrollOffsetX);
            _clipCanvasPanel.SetScrollOffset(_scrollOffsetX);
        }
    }

    /// <summary>
    /// 트랙 업데이트
    /// </summary>
    private void UpdateTracks()
    {
        if (_viewModel == null) return;

        _trackListPanel.SetTracks(_viewModel.VideoTracks, _viewModel.AudioTracks);
        _clipCanvasPanel.SetTracks(_viewModel.VideoTracks.ToList(), _viewModel.AudioTracks.ToList());
    }

    /// <summary>
    /// 클립 업데이트
    /// </summary>
    private void UpdateClips()
    {
        if (_viewModel == null)
        {
            System.Diagnostics.Debug.WriteLine("⚠️ TimelineCanvas.UpdateClips: _viewModel is null!");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"🔄 TimelineCanvas.UpdateClips called! Clips.Count = {_viewModel.Clips.Count}");
        _clipCanvasPanel.SetClips(_viewModel.Clips);
        System.Diagnostics.Debug.WriteLine($"   ✅ ClipCanvasPanel.SetClips executed");
    }

}

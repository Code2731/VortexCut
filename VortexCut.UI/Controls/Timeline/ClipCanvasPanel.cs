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
using VortexCut.UI.Services.Actions;
using VortexCut.Interop.Services;

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
/// partial class — 분할 파일:
///   ClipCanvasPanel.Rendering.cs   — Render(), 트랙/Playhead/성능/Snap 렌더링
///   ClipCanvasPanel.ClipRendering.cs — DrawClip, 썸네일, 웨이브폼, 트랜지션 오버레이
///   ClipCanvasPanel.Keyframes.cs   — 키프레임 렌더링 + 링크 연결선
///   ClipCanvasPanel.Input.cs       — 마우스 입력 (클릭/드래그/트림/줌)
///   ClipCanvasPanel.DragDrop.cs    — Drag & Drop 처리
///   ClipCanvasPanel.Helpers.cs     — 좌표 변환, 트랙 유틸리티, 포맷
/// </summary>
public partial class ClipCanvasPanel : Control
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
    private ClipModel? _hoveredClip;  // 호버된 클립 (하이라이트용)
    private Point _dragStartPoint;
    private bool _isDragging;
    private bool _isPanning;
    private Point _panStartPoint;
    private long _lastSnappedTimeMs = -1;
    private bool _isTrimming;
    private ClipEdge _trimEdge = ClipEdge.None;
    private ClipEdge _hoveredEdge = ClipEdge.None; // 호버된 트림 에지 (핸들 하이라이트용)
    private long _originalStartTimeMs;
    private long _originalDurationMs;
    private int _originalTrackIndex; // Undo용 원래 트랙 인덱스

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

    // 자막 트랙
    private List<TrackModel> _subtitleTracks = new();

    // 호버 썸네일 프리뷰 (A)
    private ThumbnailSession? _hoverThumbnailSession;
    private string? _hoverSessionFilePath; // 세션 재사용 판단용
    private Avalonia.Media.Imaging.WriteableBitmap? _hoverThumbnailBitmap;
    private long _hoverThumbnailTimeMs = -1; // 현재 표시 중인 썸네일 시간
    private Point _hoverThumbnailPos;        // 마우스 위치 (렌더 좌표)
    private bool _hoverThumbnailVisible;
    private CancellationTokenSource? _hoverDebounceTokenSource;
    private int _hoverThumbnailRenderActive; // Interlocked 가드

    // 트림 프리뷰 오버레이 (G)
    private Avalonia.Media.Imaging.WriteableBitmap? _trimPreviewBitmap;
    private long _trimPreviewTimeMs = -1;
    private bool _trimPreviewVisible;
    private int _trimPreviewRenderActive; // Interlocked 가드

    // Swifter 스크럽 썸네일 그리드 (B)
    private bool _isScrubbing;                 // 빈 공간 클릭+드래그 중
    private bool _scrubGridVisible;
    private Avalonia.Media.Imaging.WriteableBitmap?[] _scrubGridBitmaps = new Avalonia.Media.Imaging.WriteableBitmap?[8];
    private long[] _scrubGridTimeMs = new long[8];
    private long _scrubGridLastUpdateMs = -1;  // 마지막 갱신 시간
    private int _scrubGridRenderActive;        // Interlocked 가드
    private double _scrubGridY;                // 그리드 Y 위치

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
        if (e.PropertyName == nameof(TimelineViewModel.IsPlaying))
        {
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
        else if (e.PropertyName == nameof(TimelineViewModel.CurrentTimeMs))
        {
            // 재생 중 타임라인 리드로 스로틀: 플레이헤드가 2px 이상 이동했을 때만
            if (_viewModel != null)
            {
                var newX = _viewModel.CurrentTimeMs * _pixelsPerMs - _scrollOffsetX;
                var oldX = _lastPlayheadTimeMs * _pixelsPerMs - _scrollOffsetX;
                if (Math.Abs(newX - oldX) >= 2.0)
                {
                    _lastPlayheadTimeMs = _viewModel.CurrentTimeMs;
                    Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
                }
            }
        }
    }

    public void SetClips(IEnumerable<ClipModel> clips)
    {
        _clips = new List<ClipModel>(clips);
        InvalidateVisual();
    }

    public void SetTracks(List<TrackModel> videoTracks, List<TrackModel> audioTracks, List<TrackModel>? subtitleTracks = null)
    {
        _videoTracks = videoTracks;
        _audioTracks = audioTracks;
        _subtitleTracks = subtitleTracks ?? new List<TrackModel>();
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
}

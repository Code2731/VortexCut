using System.Timers;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Models;
using VortexCut.Interop.Services;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// Clip Monitor(Source Monitor) ViewModel
/// Project Bin에서 선택한 클립의 독립 프리뷰 + In/Out 마크 + 타임라인 삽입
/// ThumbnailSession을 프리뷰 해상도(960x540)로 사용하여 프레임 렌더링
/// </summary>
public partial class SourceMonitorViewModel : ViewModelBase, IDisposable
{
    private ThumbnailSession? _session;

    // 더블 버퍼링: 두 비트맵을 교대 사용 → 참조 변경으로 Image 바인딩 강제 갱신
    private WriteableBitmap? _bitmapA;
    private WriteableBitmap? _bitmapB;
    private bool _useA = true;
    private int _bitmapWidth;
    private int _bitmapHeight;

    // 30fps 재생 타이머
    private readonly System.Timers.Timer _playbackTimer;

    // Stopwatch 기반 플레이백 클럭 (누적 오차 방지)
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private long _playbackStartTimeMs;

    // 단일 렌더 슬롯: 동시에 하나의 렌더만 → ThumbnailSession 동시 접근 방지
    private int _playbackRenderActive = 0;
    private int _scrubRenderActive = 0;
    private long _pendingScrubTimeMs = -1;

    [ObservableProperty]
    private WriteableBitmap? _previewImage;

    [ObservableProperty]
    private bool _isPlaying = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTimeDisplay))]
    private long _currentTimeMs = 0;

    [ObservableProperty]
    private long _durationMs = 0;

    [ObservableProperty]
    private string _clipName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InPointDisplay))]
    private long _inPointMs = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutPointDisplay))]
    private long _outPointMs = -1;

    /// <summary>
    /// 클립이 로드되었는지 여부
    /// </summary>
    public bool HasClip => _session != null;

    /// <summary>
    /// In 포인트가 설정되었는지
    /// </summary>
    public bool HasInPoint => InPointMs >= 0;

    /// <summary>
    /// Out 포인트가 설정되었는지
    /// </summary>
    public bool HasOutPoint => OutPointMs >= 0;

    /// <summary>
    /// 타임코드 표시 (HH:MM:SS.mmm)
    /// </summary>
    public string CurrentTimeDisplay
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(CurrentTimeMs);
            return ts.ToString(@"hh\:mm\:ss\.fff");
        }
    }

    /// <summary>
    /// In 포인트 표시
    /// </summary>
    public string InPointDisplay => HasInPoint
        ? TimeSpan.FromMilliseconds(InPointMs).ToString(@"hh\:mm\:ss\.fff")
        : "--:--:--:---";

    /// <summary>
    /// Out 포인트 표시
    /// </summary>
    public string OutPointDisplay => HasOutPoint
        ? TimeSpan.FromMilliseconds(OutPointMs).ToString(@"hh\:mm\:ss\.fff")
        : "--:--:--:---";

    /// <summary>
    /// 로드된 미디어 아이템 (Add to Timeline 시 필요)
    /// </summary>
    public MediaItem? LoadedItem { get; private set; }

    public SourceMonitorViewModel()
    {
        _playbackTimer = new System.Timers.Timer(1000.0 / 30.0);
        _playbackTimer.Elapsed += OnPlaybackTick;
    }

    /// <summary>
    /// 클립 로드 (Project Bin 더블클릭 시 호출)
    /// </summary>
    public void LoadClip(MediaItem item)
    {
        // 재생 중이면 정지
        if (IsPlaying)
            TogglePlayback();

        // 기존 세션 정리
        _session?.Dispose();
        _session = null;

        try
        {
            // 프리뷰 해상도로 ThumbnailSession 생성
            _session = ThumbnailSession.Create(item.FilePath, 960, 540);
            LoadedItem = item;
            ClipName = item.Name;
            DurationMs = _session.DurationMs > 0 ? _session.DurationMs : item.DurationMs;
            CurrentTimeMs = 0;
            InPointMs = -1;
            OutPointMs = -1;

            OnPropertyChanged(nameof(HasClip));
            OnPropertyChanged(nameof(HasInPoint));
            OnPropertyChanged(nameof(HasOutPoint));

            // 첫 프레임 렌더
            RenderFrameAsync(0);

            DebugLogger.Log($"[SOURCE] Loaded clip: {item.Name}, duration={DurationMs}ms");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[SOURCE] Failed to load clip: {ex.Message}");
            _session = null;
            LoadedItem = null;
            ClipName = string.Empty;
            DurationMs = 0;
            OnPropertyChanged(nameof(HasClip));
        }
    }

    /// <summary>
    /// 스크럽 프레임 렌더링 (단일 슬롯 스로틀링)
    /// </summary>
    public void RenderFrameAsync(long timestampMs)
    {
        if (_session == null) return;

        // 재생 중이면 스크럽 무시
        if (IsPlaying) return;

        // 최신 요청 timestamp 기록
        Interlocked.Exchange(ref _pendingScrubTimeMs, timestampMs);

        // 단일 슬롯: 이전 렌더 진행 중이면 건너뜀
        if (Interlocked.CompareExchange(ref _scrubRenderActive, 1, 0) != 0)
            return;

        _ = ScrubRenderLoop();
    }

    /// <summary>
    /// 스크럽 렌더 루프: pending timestamp를 렌더하고,
    /// 완료 후 새 pending이 있으면 마지막 것만 렌더
    /// </summary>
    private async Task ScrubRenderLoop()
    {
        try
        {
            while (true)
            {
                long ts = Interlocked.Exchange(ref _pendingScrubTimeMs, -1);
                if (ts < 0) break;

                byte[]? frameData = null;
                uint width = 0, height = 0;

                await Task.Run(() =>
                {
                    try
                    {
                        using var frame = _session?.Generate(ts);
                        if (frame != null)
                        {
                            frameData = frame.Data; // ToArray() 제거 — Data는 관리 메모리, Dispose 후에도 유효
                            width = frame.Width;
                            height = frame.Height;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[SOURCE SCRUB] Generate threw at {ts}ms: {ex.Message}");
                    }
                });

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (frameData != null)
                    {
                        try { UpdateBitmap(frameData, width, height); }
                        catch (Exception ex) { DebugLogger.Log($"[SOURCE] Bitmap error: {ex.Message}"); }
                    }
                    CurrentTimeMs = ts;
                });

                if (Interlocked.Read(ref _pendingScrubTimeMs) < 0) break;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[SOURCE SCRUB] Render loop error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _scrubRenderActive, 0);

            // 슬롯 해제 후 새 pending이 있으면 재시작
            if (Interlocked.Read(ref _pendingScrubTimeMs) >= 0)
            {
                if (Interlocked.CompareExchange(ref _scrubRenderActive, 1, 0) == 0)
                {
                    _ = ScrubRenderLoop();
                }
            }
        }
    }

    /// <summary>
    /// 재생/정지 토글
    /// </summary>
    [RelayCommand]
    public void TogglePlayback()
    {
        if (_session == null) return;

        if (IsPlaying)
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            IsPlaying = false;
        }
        else
        {
            // IsPlaying을 먼저 설정해야 TwoWay 바인딩이 스크럽 렌더를 차단함
            // (CurrentTimeMs 변경 → Slider ValueChanged → RenderFrameAsync에서 IsPlaying 체크)
            IsPlaying = true;

            // 영상 끝 근처에서 재생 시 → 처음부터
            if (CurrentTimeMs >= DurationMs - 500)
            {
                CurrentTimeMs = 0;
            }

            _playbackStartTimeMs = CurrentTimeMs;
            Interlocked.Exchange(ref _playbackRenderActive, 0);
            _playbackClock.Restart();
            _playbackTimer.Start();
        }
    }

    /// <summary>
    /// 재생 타이머 틱 (단일 렌더 슬롯)
    /// </summary>
    private void OnPlaybackTick(object? sender, ElapsedEventArgs e)
    {
        var newTimeMs = _playbackStartTimeMs + _playbackClock.ElapsedMilliseconds;

        // 클립 끝 도달 시 정지
        if (newTimeMs >= DurationMs)
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                CurrentTimeMs = DurationMs;
            });
            return;
        }

        // 시간 업데이트 (UI 스레드)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentTimeMs = newTimeMs;
        });

        // 단일 렌더 슬롯
        if (Interlocked.CompareExchange(ref _playbackRenderActive, 1, 0) == 0)
        {
            _ = PlaybackRenderAsync(newTimeMs);
        }
    }

    /// <summary>
    /// 재생 전용 프레임 렌더링
    /// </summary>
    private async Task PlaybackRenderAsync(long timestampMs)
    {
        try
        {
            byte[]? frameData = null;
            uint width = 0, height = 0;

            await Task.Run(() =>
            {
                try
                {
                    using var frame = _session?.Generate(timestampMs);
                    if (frame != null)
                    {
                        frameData = frame.Data; // ToArray() 제거 — Data는 관리 메모리, Dispose 후에도 유효
                        width = frame.Width;
                        height = frame.Height;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[SOURCE PLAY] Generate threw at {timestampMs}ms: {ex.Message}");
                }
            });

            if (frameData != null)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { UpdateBitmap(frameData, width, height); }
                    catch (Exception ex) { DebugLogger.Log($"[SOURCE] Bitmap error: {ex.Message}"); }
                });
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[SOURCE PLAY] Render error at {timestampMs}ms: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _playbackRenderActive, 0);
        }
    }

    /// <summary>
    /// 비트맵 업데이트 (UI 스레드에서 호출)
    /// 더블 버퍼링: A/B 교대 사용
    /// </summary>
    private void UpdateBitmap(byte[] frameData, uint width, uint height)
    {
        if (_bitmapWidth != (int)width || _bitmapHeight != (int)height)
        {
            var pixelSize = new Avalonia.PixelSize((int)width, (int)height);
            var dpi = new Avalonia.Vector(96, 96);
            _bitmapA = new WriteableBitmap(pixelSize, dpi,
                Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
            _bitmapB = new WriteableBitmap(pixelSize, dpi,
                Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
            _bitmapWidth = (int)width;
            _bitmapHeight = (int)height;
        }

        var target = _useA ? _bitmapA! : _bitmapB!;
        _useA = !_useA;

        using (var buffer = target.Lock())
        {
            unsafe
            {
                fixed (byte* srcPtr = frameData)
                {
                    var dst = (byte*)buffer.Address;
                    var size = (int)width * (int)height * 4;
                    Buffer.MemoryCopy(srcPtr, dst, size, size);
                }
            }
        }

        PreviewImage = target;
    }

    /// <summary>
    /// Mark In — 현재 시간을 In 포인트로 설정
    /// </summary>
    [RelayCommand]
    private void MarkIn()
    {
        if (_session == null) return;
        InPointMs = CurrentTimeMs;
        OnPropertyChanged(nameof(HasInPoint));
    }

    /// <summary>
    /// Mark Out — 현재 시간을 Out 포인트로 설정
    /// </summary>
    [RelayCommand]
    private void MarkOut()
    {
        if (_session == null) return;
        OutPointMs = CurrentTimeMs;
        OnPropertyChanged(nameof(HasOutPoint));
    }

    /// <summary>
    /// In/Out 범위 반환 (trimStartMs, durationMs)
    /// In/Out 미설정 시 전체 클립 범위 반환
    /// </summary>
    public (long trimStartMs, long durationMs) GetInsertRange()
    {
        long inMs = HasInPoint ? InPointMs : 0;
        long outMs = HasOutPoint ? OutPointMs : DurationMs;

        // In > Out 방지
        if (inMs > outMs)
            (inMs, outMs) = (outMs, inMs);

        return (inMs, outMs - inMs);
    }

    /// <summary>
    /// 초기화
    /// </summary>
    public void Reset()
    {
        if (IsPlaying)
            TogglePlayback();

        _session?.Dispose();
        _session = null;
        LoadedItem = null;
        ClipName = string.Empty;
        DurationMs = 0;
        CurrentTimeMs = 0;
        InPointMs = -1;
        OutPointMs = -1;
        _bitmapA = null;
        _bitmapB = null;
        _useA = true;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
        PreviewImage = null;

        OnPropertyChanged(nameof(HasClip));
        OnPropertyChanged(nameof(HasInPoint));
        OnPropertyChanged(nameof(HasOutPoint));
    }

    public void Dispose()
    {
        _playbackTimer?.Dispose();
        _session?.Dispose();
        GC.SuppressFinalize(this);
    }
}

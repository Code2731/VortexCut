using System.Timers;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Interop.Services;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// 프리뷰 ViewModel
/// </summary>
public partial class PreviewViewModel : ViewModelBase, IDisposable
{
    private readonly ProjectService _projectService;
    private readonly System.Timers.Timer _playbackTimer;

    [ObservableProperty]
    private bool _isPlaying = false;

    private TimelineViewModel? _timelineViewModel; // Timeline 참조
    private volatile bool _isRendering = false; // 렌더링 동시성 제어

    // Stopwatch 기반 플레이백 클럭 (누적 오차 방지)
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private long _playbackStartTimeMs; // 재생 시작 시점의 타임라인 위치

    // 더블 버퍼링: 두 비트맵을 교대 사용 → 참조 변경으로 Image 바인딩 강제 갱신
    private WriteableBitmap? _bitmapA;
    private WriteableBitmap? _bitmapB;
    private bool _useA = true;
    private int _bitmapWidth;
    private int _bitmapHeight;

    [ObservableProperty]
    private WriteableBitmap? _previewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTimeDisplay))]
    private long _currentTimeMs = 0;

    /// <summary>
    /// 타임코드 표시용 포맷 문자열 (HH:MM:SS.mmm)
    /// </summary>
    public string CurrentTimeDisplay
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(CurrentTimeMs);
            return ts.ToString(@"hh\:mm\:ss\.fff");
        }
    }

    [ObservableProperty]
    private bool _isLoading = false;

    public PreviewViewModel(ProjectService projectService)
    {
        _projectService = projectService;

        // 30fps 재생 타이머
        _playbackTimer = new System.Timers.Timer(1000.0 / 30.0);
        _playbackTimer.Elapsed += OnPlaybackTick;
    }

    /// <summary>
    /// TimelineViewModel 연결 (MainViewModel에서 호출)
    /// </summary>
    public void SetTimelineViewModel(TimelineViewModel timelineViewModel)
    {
        _timelineViewModel = timelineViewModel;
    }

    /// <summary>
    /// 특정 시간의 프레임 렌더링
    /// NOTE: Rust Renderer가 Mutex로 보호되므로 C# 측에서 동기화 불필요
    /// </summary>
    public async Task RenderFrameAsync(long timestampMs)
    {
        try
        {
            byte[]? frameData = null;
            uint width = 0, height = 0;

            await Task.Run(() =>
            {
                using var frame = _projectService.RenderFrame(timestampMs);
                if (frame != null)
                {
                    frameData = frame.Data.ToArray();
                    width = frame.Width;
                    height = frame.Height;
                }
            });

            // UI 스레드에서 비트맵 + 시간 업데이트
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (frameData != null)
                {
                    try { UpdateBitmap(frameData, width, height); }
                    catch (Exception ex) { Services.DebugLogger.Log($"Bitmap error: {ex.Message}"); }
                }
                CurrentTimeMs = timestampMs;
            });
        }
        catch (Exception ex)
        {
            Services.DebugLogger.Log($"RenderFrameAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// 비트맵 업데이트 (UI 스레드에서 호출해야 함)
    /// 더블 버퍼링: A/B 두 비트맵을 교대 사용하여 매 프레임 참조 변경
    /// → Avalonia Image 바인딩이 새 객체를 감지하여 화면 갱신
    /// </summary>
    private void UpdateBitmap(byte[] frameData, uint width, uint height)
    {
        // 해상도 변경 시 양쪽 버퍼 모두 재생성
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

        // 이번 프레임에 사용할 버퍼 선택 (이전과 다른 버퍼)
        var target = _useA ? _bitmapA! : _bitmapB!;
        _useA = !_useA;

        // Lock → 픽셀 복사 → Unlock
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

        // 항상 다른 객체 참조 → Avalonia가 새 이미지로 인식하여 렌더링
        PreviewImage = target;
    }

    /// <summary>
    /// 재생/일시정지 토글
    /// </summary>
    public void TogglePlayback()
    {
        System.Diagnostics.Debug.WriteLine($"▶️ TogglePlayback called! Current IsPlaying={IsPlaying}");

        if (IsPlaying)
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            IsPlaying = false;
        }
        else
        {
            // 클립이 없으면 재생하지 않음
            if (_timelineViewModel == null || _timelineViewModel.Clips.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("   ⚠️ No clips to play!");
                return;
            }

            System.Diagnostics.Debug.WriteLine("   Starting playback...");
            // 재생 시작: Timeline의 현재 시간부터 시작
            if (_timelineViewModel != null)
            {
                CurrentTimeMs = _timelineViewModel.CurrentTimeMs;
            }
            // Stopwatch 기반 클럭 시작 (누적 오차 방지)
            _playbackStartTimeMs = CurrentTimeMs;
            _playbackClock.Restart();
            _playbackTimer.Start();
            IsPlaying = true;
        }
    }

    /// <summary>
    /// 재생 타이머 틱 (INTERACTION_FLOWS.md: Latencyless Feel)
    /// </summary>
    private void OnPlaybackTick(object? sender, ElapsedEventArgs e)
    {
        // Stopwatch 기반 실제 경과 시간 계산 (누적 오차 없음)
        var newTimeMs = _playbackStartTimeMs + _playbackClock.ElapsedMilliseconds;

        // 클립 끝 감지: 재생 시간이 모든 클립의 끝을 넘으면 정지
        if (_timelineViewModel != null && _timelineViewModel.Clips.Count > 0)
        {
            long maxEndTime = 0;
            foreach (var clip in _timelineViewModel.Clips)
            {
                var clipEnd = clip.StartTimeMs + clip.DurationMs;
                if (clipEnd > maxEndTime) maxEndTime = clipEnd;
            }

            if (newTimeMs >= maxEndTime)
            {
                _playbackTimer.Stop();
                _playbackClock.Stop();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsPlaying = false;
                    CurrentTimeMs = maxEndTime;
                    if (_timelineViewModel != null)
                        _timelineViewModel.CurrentTimeMs = maxEndTime;
                });
                return;
            }
        }

        // CRITICAL: PropertyChanged → XAML 바인딩 업데이트는 UI 스레드에서만 가능
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentTimeMs = newTimeMs;
            if (_timelineViewModel != null)
            {
                _timelineViewModel.CurrentTimeMs = newTimeMs;
            }
        });

        // 렌더링 동시성 제어: 이전 프레임 렌더링 중이면 스킵 (프레임 누적 방지)
        if (_isRendering) return;
        _isRendering = true;

        _ = Task.Run(() =>
        {
            try
            {
                // Rust FFI 렌더링 + 데이터 복사 (배경 스레드, ~2ms)
                byte[]? frameData = null;
                uint width = 0, height = 0;

                using var frame = _projectService.RenderFrame(newTimeMs);
                if (frame != null)
                {
                    frameData = frame.Data.ToArray();
                    width = frame.Width;
                    height = frame.Height;
                }

                // Rust 렌더 완료 → 즉시 플래그 해제 (다음 프레임 렌더링 가능)
                _isRendering = false;

                // UI 업데이트는 Post로 fire-and-forget (UI 스레드 대기 안 함)
                if (frameData != null)
                {
                    var data = frameData;
                    var w = width;
                    var h = height;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            UpdateBitmap(data, w, h);
                        }
                        catch (Exception ex)
                        {
                            Services.DebugLogger.Log($"Bitmap update error: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _isRendering = false;
                Services.DebugLogger.Log($"Playback render error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 초기화
    /// </summary>
    public void Reset()
    {
        _playbackTimer.Stop();
        IsPlaying = false;
        CurrentTimeMs = 0;
        _bitmapA = null;
        _bitmapB = null;
        _useA = true;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
        PreviewImage = null;
    }

    public void Dispose()
    {
        _playbackTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private async Task SeekAsync(long timestampMs)
    {
        await RenderFrameAsync(timestampMs);
    }
}

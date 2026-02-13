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
    private readonly AudioPlaybackService _audioPlayback = new();
    private readonly System.Timers.Timer _playbackTimer;

    [ObservableProperty]
    private bool _isPlaying = false;

    private TimelineViewModel? _timelineViewModel; // Timeline 참조

    // Stopwatch 기반 플레이백 클럭 (누적 오차 방지)
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private long _playbackStartTimeMs; // 재생 시작 시점의 타임라인 위치

    // 단일 렌더 슬롯: 동시에 하나의 렌더만 진행 → Mutex 경합 방지
    // 0=idle, 1=active. Interlocked.CompareExchange로 원자적 전환
    private int _playbackRenderActive = 0;

    // 스크럽 렌더 슬롯: 스크럽도 동시 1개만 실행 → try_lock 경합 + FFmpeg 재진입 방지
    private int _scrubRenderActive = 0;
    // 스크럽 최신 요청 timestamp: 렌더 완료 후 새 요청이 있으면 마지막 것만 실행
    private long _pendingScrubTimeMs = -1;

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
    /// 특정 시간의 프레임 렌더링 (스크럽/시크 용)
    /// 단일 슬롯 스로틀링: 이전 렌더 진행 중이면 최신 timestamp만 기록하고 건너뜀
    /// → FFmpeg 재진입 방지 + try_lock 경합 방지 + 패킷 큐 폭증 방지
    /// </summary>
    public void RenderFrameAsync(long timestampMs)
    {
        // 재생 중이면 스크럽 렌더 무시 (PlaybackRenderAsync가 처리)
        if (IsPlaying) return;

        // 스크럽 시 오디오 정지 (다음 재생 시 새 위치에서 시작)
        if (_audioPlayback.IsActive)
            _audioPlayback.Stop();

        // 최신 요청 timestamp 기록 (이전 요청 덮어쓰기)
        Interlocked.Exchange(ref _pendingScrubTimeMs, timestampMs);

        // 단일 슬롯: 이전 렌더 진행 중이면 건너뜀 (완료 후 pending 확인)
        if (Interlocked.CompareExchange(ref _scrubRenderActive, 1, 0) != 0)
            return;

        _ = ScrubRenderLoop();
    }

    /// <summary>
    /// 스크럽 렌더 루프: 현재 pending timestamp를 렌더하고,
    /// 완료 후 새 pending이 있으면 마지막 것만 렌더 (중간 프레임 건너뜀)
    /// </summary>
    private async Task ScrubRenderLoop()
    {
        try
        {
            while (true)
            {
                // pending timestamp 가져오고 초기화
                long ts = Interlocked.Exchange(ref _pendingScrubTimeMs, -1);
                if (ts < 0) break; // 더 이상 pending 없음

                byte[]? frameData = null;
                uint width = 0, height = 0;

                await Task.Run(() =>
                {
                    try
                    {
                        using var frame = _projectService.RenderFrame(ts);
                        if (frame != null)
                        {
                            frameData = frame.Data.ToArray();
                            width = frame.Width;
                            height = frame.Height;
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.DebugLogger.Log($"[SCRUB] RenderFrame threw at {ts}ms: {ex.Message}");
                    }
                });

                // UI 스레드에서 비트맵 업데이트
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (frameData != null)
                    {
                        try { UpdateBitmap(frameData, width, height); }
                        catch (Exception ex) { Services.DebugLogger.Log($"Bitmap error: {ex.Message}"); }
                    }
                    CurrentTimeMs = ts;
                });

                // 새 pending이 들어왔는지 확인 → 있으면 계속
                if (Interlocked.Read(ref _pendingScrubTimeMs) < 0) break;
            }
        }
        catch (Exception ex)
        {
            Services.DebugLogger.Log($"[SCRUB] Render loop error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _scrubRenderActive, 0);

            // 슬롯 해제 후 또 pending이 들어왔으면 재시작
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
    /// 재생 전용 프레임 렌더링 (단일 렌더 슬롯)
    /// 이전 렌더가 완료된 후에만 다음 렌더 시작 → Rust Mutex 경합 없음
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
                    using var frame = _projectService.RenderFrame(timestampMs);
                    if (frame != null)
                    {
                        frameData = frame.Data.ToArray();
                        width = frame.Width;
                        height = frame.Height;
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.Log($"[PLAYBACK] RenderFrame threw at {timestampMs}ms: {ex.Message}");
                }
            });

            // UI 스레드에서 비트맵만 업데이트 (시간은 OnPlaybackTick에서 이미 업데이트)
            if (frameData != null)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { UpdateBitmap(frameData, width, height); }
                    catch (Exception ex) { Services.DebugLogger.Log($"Bitmap error: {ex.Message}"); }
                });
            }
        }
        catch (Exception ex)
        {
            Services.DebugLogger.Log($"[PLAYBACK] Render error at {timestampMs}ms: {ex.Message}");
        }
        finally
        {
            // 렌더 슬롯 해제 → 다음 틱에서 새 렌더 시작 가능
            Interlocked.Exchange(ref _playbackRenderActive, 0);
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
        Services.DebugLogger.Log($"[PLAY] TogglePlayback: IsPlaying={IsPlaying}");

        if (IsPlaying)
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            _audioPlayback.Stop(); // 오디오 정지 (매번 새로 시작하여 동기화 보장)
            _projectService.SetPlaybackMode(false); // 스크럽 모드로 전환
            IsPlaying = false;
            Services.DebugLogger.Log("[PLAY] Stopped");
        }
        else
        {
            // 클립이 없으면 재생하지 않음
            if (_timelineViewModel == null || _timelineViewModel.Clips.Count == 0)
            {
                Services.DebugLogger.Log("[PLAY] No clips! Aborting.");
                return;
            }

            // 재생 시작: Timeline의 현재 시간부터 시작
            if (_timelineViewModel != null)
            {
                CurrentTimeMs = _timelineViewModel.CurrentTimeMs;
            }

            // maxEndTime 계산
            long maxEndTime = 0;
            foreach (var clip in _timelineViewModel!.Clips)
            {
                var clipEnd = clip.StartTimeMs + clip.DurationMs;
                if (clipEnd > maxEndTime) maxEndTime = clipEnd;
            }

            Services.DebugLogger.Log($"[PLAY] CurrentTimeMs={CurrentTimeMs}, maxEndTime={maxEndTime}, remaining={maxEndTime - CurrentTimeMs}ms");

            // 영상 끝 근처(500ms 이내)에서 재생 시 → 자동으로 처음부터 재생 (표준 NLE 동작)
            if (CurrentTimeMs >= maxEndTime - 500)
            {
                Services.DebugLogger.Log($"[PLAY] Near end, wrapping to 0");
                CurrentTimeMs = 0;
                _timelineViewModel.CurrentTimeMs = 0;
            }

            // Stopwatch 기반 클럭 시작 (누적 오차 방지)
            _playbackStartTimeMs = CurrentTimeMs;
            Interlocked.Exchange(ref _playbackRenderActive, 0); // 렌더 슬롯 초기화
            _projectService.SetPlaybackMode(true); // 재생 모드로 전환 (forward_threshold=5000ms)

            // 오디오 재생 시작 (항상 현재 위치에서 새로 시작 → 비디오와 동기화 보장)
            _audioPlayback.Start(_projectService.TimelineRawHandle, _playbackStartTimeMs);

            _playbackClock.Restart();
            _playbackTimer.Start();
            IsPlaying = true;
            Services.DebugLogger.Log($"[PLAY] Started from {_playbackStartTimeMs}ms");
        }
    }

    /// <summary>
    /// 재생 타이머 틱
    /// 핵심: 단일 렌더 슬롯 패턴으로 Mutex 경합 방지
    /// - 이전 렌더가 완료되지 않았으면 이번 틱의 렌더는 건너뜀
    /// - Mutex는 항상 즉시 획득 가능 → try_lock 100% 성공
    /// - 디코더 위치가 항상 최신 → 순차 디코딩으로 빠른 렌더
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
                _audioPlayback.Stop(); // 오디오 정지
                _projectService.SetPlaybackMode(false); // 스크럽 모드로 복귀
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

        // 타임라인 시간 업데이트 (UI 스레드)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentTimeMs = newTimeMs;
            if (_timelineViewModel != null)
            {
                _timelineViewModel.CurrentTimeMs = newTimeMs;
            }
        });

        // 단일 렌더 슬롯: 이전 렌더가 완료된 경우에만 새 렌더 시작
        // CompareExchange(ref active, 1, 0): active가 0이면 1로 바꾸고 0 반환 (→ 시작)
        //                                    active가 1이면 그대로 1 반환 (→ 건너뜀)
        if (Interlocked.CompareExchange(ref _playbackRenderActive, 1, 0) == 0)
        {
            _ = PlaybackRenderAsync(newTimeMs);
        }
    }

    /// <summary>
    /// 초기화
    /// </summary>
    public void Reset()
    {
        _playbackTimer.Stop();
        _audioPlayback.Stop(); // 오디오 정지
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
        _audioPlayback.Dispose(); // 오디오 정리
        _playbackTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private void Seek(long timestampMs)
    {
        RenderFrameAsync(timestampMs);
    }
}

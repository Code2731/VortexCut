using System.Timers;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Interfaces;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// 프리뷰 ViewModel
/// </summary>
public partial class PreviewViewModel : ViewModelBase, IDisposable
{
    private readonly IProjectService _projectService;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly System.Timers.Timer _playbackTimer;

    [ObservableProperty]
    private bool _isPlaying = false;

    private TimelineViewModel? _timelineViewModel; // Timeline 참조

    // Stopwatch 기반 플레이백 클럭 (누적 오차 방지)
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private long _playbackStartTimeMs; // 재생 시작 시점의 타임라인 위치
    private long _playbackMaxEndTimeMs; // 재생 시작 시 계산된 클립 끝 시간 (매 틱 반복 계산 방지)

    // 단일 렌더 슬롯: 동시에 하나의 렌더만 진행 → Mutex 경합 방지
    // 0=idle, 1=active. Interlocked.CompareExchange로 원자적 전환
    private int _playbackRenderActive = 0;

    // 타임라인 업데이트 쓰로틀: ClipCanvasPanel 재그리기 빈도 제한
    // 30fps 재그리기는 UI 스레드 과부하 → 10fps로 제한 (100ms 간격)
    private long _lastTimelineUpdateMs = 0;

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
    [NotifyPropertyChangedFor(nameof(CurrentSubtitleText))]
    [NotifyPropertyChangedFor(nameof(HasSubtitle))]
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

    // FPS 측정용
    private int _frameCount = 0;
    private System.Diagnostics.Stopwatch _fpsStopwatch = System.Diagnostics.Stopwatch.StartNew();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FpsDisplay))]
    private double _currentFps = 0.0;

    /// <summary>
    /// FPS 표시용 (소수점 1자리)
    /// </summary>
    public string FpsDisplay => $"{CurrentFps:F1}";

    /// <summary>
    /// 현재 시간에 표시할 자막 텍스트 (없으면 null)
    /// </summary>
    public string? CurrentSubtitleText =>
        _timelineViewModel?.GetSubtitleTextAt(CurrentTimeMs);

    /// <summary>
    /// 자막이 표시 중인지 여부
    /// </summary>
    public bool HasSubtitle => CurrentSubtitleText != null;

    /// <summary>
    /// 자막 텍스트/스타일 변경 시 오버레이 강제 갱신
    /// (CurrentTimeMs가 변하지 않아도 프리뷰 자막 표시 업데이트)
    /// </summary>
    public void RefreshSubtitleOverlay()
    {
        OnPropertyChanged(nameof(CurrentSubtitleText));
        OnPropertyChanged(nameof(HasSubtitle));
    }

    // 재생 속도 (0.25x ~ 4x)
    private static readonly double[] SpeedSteps = { 0.25, 0.5, 1.0, 1.5, 2.0, 4.0 };
    private int _speedIndex = 2; // 기본 1.0x

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedDisplayText))]
    private double _playbackSpeed = 1.0;

    /// <summary>
    /// 속도 표시 텍스트 (버튼용)
    /// </summary>
    public string SpeedDisplayText => $"{PlaybackSpeed}x";

    /// <summary>
    /// 재생 속도 순환 (0.25x → 0.5x → 1x → 1.5x → 2x → 4x → 0.25x)
    /// </summary>
    [RelayCommand]
    private void CyclePlaybackSpeed()
    {
        _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
        PlaybackSpeed = SpeedSteps[_speedIndex];
    }

    public PreviewViewModel(IProjectService projectService, IAudioPlaybackService audioPlayback)
    {
        _projectService = projectService;
        _audioPlayback = audioPlayback;

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
                            frameData = frame.Data; // ToArray() 제거 — 이미 관리 메모리 byte[], 추가 할당 불필요
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
                        frameData = frame.Data; // ToArray() 제거 — 이미 관리 메모리 byte[], 추가 할당 불필요
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

        // FPS 측정
        _frameCount++;
        var fpsElapsed = _fpsStopwatch.ElapsedMilliseconds;
        if (fpsElapsed >= 1000)
        {
            CurrentFps = _frameCount * 1000.0 / fpsElapsed;
            _frameCount = 0;
            _fpsStopwatch.Restart();
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
            // 정지 시 타임라인 시간 확실히 동기화 (쓰로틀로 누락될 수 있으므로)
            if (_timelineViewModel != null)
                _timelineViewModel.CurrentTimeMs = CurrentTimeMs;
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

            // maxEndTime 계산 (재생 시작 시 1회만, 캐시하여 매 틱 반복 계산 방지)
            _playbackMaxEndTimeMs = 0;
            foreach (var clip in _timelineViewModel!.Clips)
            {
                var clipEnd = clip.StartTimeMs + clip.DurationMs;
                if (clipEnd > _playbackMaxEndTimeMs) _playbackMaxEndTimeMs = clipEnd;
            }

            Services.DebugLogger.Log($"[PLAY] CurrentTimeMs={CurrentTimeMs}, maxEndTime={_playbackMaxEndTimeMs}, remaining={_playbackMaxEndTimeMs - CurrentTimeMs}ms");

            // 영상 끝 근처(500ms 이내)에서 재생 시 → 자동으로 처음부터 재생 (표준 NLE 동작)
            if (CurrentTimeMs >= _playbackMaxEndTimeMs - 500)
            {
                Services.DebugLogger.Log($"[PLAY] Near end, wrapping to 0");
                CurrentTimeMs = 0;
                _timelineViewModel.CurrentTimeMs = 0;
            }

            // Stopwatch 기반 클럭 시작 (누적 오차 방지)
            _playbackStartTimeMs = CurrentTimeMs;
            Interlocked.Exchange(ref _playbackRenderActive, 0); // 렌더 슬롯 초기화
            _projectService.SetPlaybackMode(true); // 재생 모드로 전환 (forward_threshold=5000ms)

            // 오디오 재생 시작 (1.0x 속도일 때만, 그 외에는 뮤트)
            if (Math.Abs(PlaybackSpeed - 1.0) < 0.01)
                _audioPlayback.Start(_projectService.TimelineRawHandle, _playbackStartTimeMs);
            else
                _audioPlayback.Stop();

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
        // Stopwatch 기반 실제 경과 시간 계산 (속도 배율 적용)
        var newTimeMs = _playbackStartTimeMs + (long)(_playbackClock.ElapsedMilliseconds * PlaybackSpeed);

        // 클립 끝 감지: 캐시된 maxEndTime 사용 (매 틱 O(n) 반복 제거)
        if (newTimeMs >= _playbackMaxEndTimeMs && _playbackMaxEndTimeMs > 0)
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            _audioPlayback.Stop(); // 오디오 정지
            _projectService.SetPlaybackMode(false); // 스크럽 모드로 복귀
            var endTime = _playbackMaxEndTimeMs;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                CurrentTimeMs = endTime;
                if (_timelineViewModel != null)
                    _timelineViewModel.CurrentTimeMs = endTime;
            });
            return;
        }

        // 타임라인 시간 업데이트 (UI 스레드)
        // 쓰로틀: TimelineViewModel.CurrentTimeMs는 100ms 간격으로만 갱신
        // → ClipCanvasPanel 재그리기 빈도 30fps→10fps로 감소 (UI 스레드 부하 대폭 절감)
        var shouldUpdateTimeline = (newTimeMs - _lastTimelineUpdateMs) >= 100;
        if (shouldUpdateTimeline) _lastTimelineUpdateMs = newTimeMs;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentTimeMs = newTimeMs;
            if (_timelineViewModel != null && shouldUpdateTimeline)
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

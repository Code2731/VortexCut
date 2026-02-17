using System.Timers;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Interfaces;
using VortexCut.UI.Services;

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

    // Stopwatch 기반 플레이백 클럭 (오디오 비활성 시 폴백용)
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private long _playbackStartTimeMs; // 재생 시작 시점의 타임라인 위치
    private long _playbackMaxEndTimeMs; // 재생 시작 시 계산된 클립 끝 시간 (매 틱 반복 계산 방지)

    // 단일 렌더 슬롯: 동시에 하나의 렌더만 진행 → Mutex 경합 방지
    // 0=idle, 1=active. Interlocked.CompareExchange로 원자적 전환
    private int _playbackRenderActive = 0;

    // 타임라인 업데이트 쓰로틀: ClipCanvasPanel 재그리기 빈도 제한
    // 30fps 재그리기는 UI 스레드 과부하 → 10fps로 제한 (100ms 간격)
    private long _lastTimelineUpdateMs = 0;

    // 렌더 파이프라인 lookahead: 비디오 디스플레이 지연 보상
    // PlaybackEngine 큐 조회(1ms) + Dispatcher(5ms) + Avalonia vsync(~16ms) ≈ 22ms
    // 비디오를 오디오보다 1프레임(33ms) 앞서 요청하여 체감 A/V 동기화 개선
    private const long RenderLookaheadMs = 33;

    // 스크럽 렌더 슬롯: 스크럽도 동시 1개만 실행 → try_lock 경합 + FFmpeg 재진입 방지
    private int _scrubRenderActive = 0;
    // 스크럽 최신 요청 timestamp: 렌더 완료 후 새 요청이 있으면 마지막 것만 실행
    private long _pendingScrubTimeMs = -1;
    // 스크럽 취소 플래그: 재생 시작 시 ScrubRenderLoop 즉시 중단
    private volatile bool _scrubCancelled = false;

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

        // 프레임 스킵: 기본 30fps (PreviewSettings.PreviewFps)
        _playbackTimer = new System.Timers.Timer(1000.0 / PreviewSettings.PreviewFps);
        _playbackTimer.Elapsed += OnPlaybackTick;
        PreviewSettings.PreviewFpsChanged += OnPreviewFpsChanged;
    }

    private void OnPreviewFpsChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _playbackTimer.Interval = 1000.0 / PreviewSettings.PreviewFps;
        });
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
        // TogglePlayback 초기화 진행 중이면 스크럽 무시
        // (ScrubRenderLoop 취소 중에 새 스크럽 시작하면 Mutex 경합 재발)
        if (_scrubCancelled) return;

        if (IsPlaying)
        {
            // 재생 중 스크럽: PlaybackEngine 큐 우선, 없으면 RenderFrame 사용
            // 타임라인 위치는 업데이트하지 않음 (오디오가 계속 재생하므로)
            Services.DebugLogger.Log($"[PLAYBACK SCRUB] Scrub during playback at {timestampMs}ms");

            Task.Run(() =>
            {
                try
                {
                    // 1. PlaybackEngine 큐에서 시도 (더 엄격한 tolerance로 정확한 프레임만 사용)
                    using var frame = _projectService.TryGetPlaybackFrame(timestampMs);
                    if (frame != null && !(frame.Data.Length >= 4 && frame.Data[3] == 0))
                    {
                        // 프레임 타임스탬프가 요청 시간과 가까운지 확인 (16ms = 1프레임 이하)
                        long timeDiff = Math.Abs(frame.TimestampMs - timestampMs);
                        if (timeDiff <= 16)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                try
                                {
                                    UpdateBitmap(frame.Data, frame.Width, frame.Height);
                                    Services.DebugLogger.Log($"[PLAYBACK SCRUB] Used PlaybackEngine frame: requested={timestampMs}ms, actual={frame.TimestampMs}ms, diff={timeDiff}ms");
                                }
                                catch (Exception ex)
                                {
                                    Services.DebugLogger.Log($"Bitmap error: {ex.Message}");
                                }
                            });
                            return;
                        }
                        else
                        {
                            Services.DebugLogger.Log($"[PLAYBACK SCRUB] PlaybackEngine frame too far: requested={timestampMs}ms, actual={frame.TimestampMs}ms, diff={timeDiff}ms > 16ms");
                        }
                    }

                    // 2. 큐에 없으면 RenderFrame 사용 (재생 모드에서도 사용 가능)
                    Services.DebugLogger.Log($"[PLAYBACK SCRUB] Queue miss, using RenderFrame at {timestampMs}ms");
                    using var fallbackFrame = _projectService.RenderFrame(timestampMs);
                    if (fallbackFrame != null)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                UpdateBitmap(fallbackFrame.Data, fallbackFrame.Width, fallbackFrame.Height);
                                Services.DebugLogger.Log($"[PLAYBACK SCRUB] Used RenderFrame fallback at {timestampMs}ms");
                            }
                            catch (Exception ex)
                            {
                                Services.DebugLogger.Log($"Bitmap error fallback: {ex.Message}");
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.Log($"[PLAYBACK SCRUB] Error at {timestampMs}ms: {ex.Message}");
                }
            });

            return;
        }

        // 정지 상태 스크럽
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
    /// 재생 중 스크럽: PlaybackEngine 큐에서 즉시 프레임 가져옴 (A/V 싱크 유지)
    /// </summary>
    private async Task RenderFrameDuringPlayback(long timestampMs)
    {
        await Task.Run(() =>
        {
            try
            {
                // 재생 중: PlaybackEngine 큐에서 프레임 가져옴
                using var frame = _projectService.TryGetPlaybackFrame(timestampMs);
                if (frame != null)
                {
                    // BLACK 프레임 건너뜀 (PlaybackEngine이 필터링했지만 안전장치)
                    if (!(frame.Data.Length >= 4 && frame.Data[3] == 0))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                UpdateBitmap(frame.Data, frame.Width, frame.Height);
                                // 재생 중 스크럽: 타임라인 시간 업데이트 (UI 동기화)
                                CurrentTimeMs = timestampMs;
                                if (_timelineViewModel != null)
                                {
                                    _timelineViewModel.CurrentTimeMs = timestampMs;
                                }
                            }
                            catch (Exception ex)
                            {
                                Services.DebugLogger.Log($"Bitmap error during playback scrub: {ex.Message}");
                            }
                        });
                        return;
                    }
                }

                // 큐에 없으면 일반 RenderFrame 사용 (fallback)
                Services.DebugLogger.Log($"[PLAYBACK SCRUB] Queue miss at {timestampMs}ms, using RenderFrame");
                using var fallbackFrame = _projectService.RenderFrame(timestampMs);
                if (fallbackFrame != null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            UpdateBitmap(fallbackFrame.Data, fallbackFrame.Width, fallbackFrame.Height);
                            CurrentTimeMs = timestampMs;
                            if (_timelineViewModel != null)
                            {
                                _timelineViewModel.CurrentTimeMs = timestampMs;
                            }
                        }
                        catch (Exception ex)
                        {
                            Services.DebugLogger.Log($"Bitmap error during playback scrub fallback: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.Log($"[PLAYBACK SCRUB] RenderFrame threw at {timestampMs}ms: {ex.Message}");
            }
        });
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
                // 재생 시작 시 취소
                if (_scrubCancelled) break;

                // pending timestamp 가져오고 초기화
                long ts = Interlocked.Exchange(ref _pendingScrubTimeMs, -1);
                if (ts < 0) break; // 더 이상 pending 없음

                byte[]? frameData = null;
                uint width = 0, height = 0;

                await Task.Run(() =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        using var frame = _projectService.RenderFrame(ts);
                        var elapsedMs = sw.ElapsedMilliseconds;
                        Services.DebugLogger.Log($"[SCRUB RENDER] ts={ts}ms, elapsed={elapsedMs}ms, frame={(frame != null ? $"{frame.Width}x{frame.Height}" : "null")}");
                        if (frame != null)
                        {
                            frameData = frame.Data;
                            width = frame.Width;
                            height = frame.Height;

                            // FFmpeg 원거리 seek 아티팩트: alpha=0x00 → 무효 프레임
                            if (frameData.Length >= 4 && frameData[3] == 0)
                            {
                                Services.DebugLogger.Log($"[SCRUB] BLACK frame at {ts}ms (alpha=0), retrying +33ms");
                                using var frame2 = _projectService.RenderFrame(ts + 33);
                                if (frame2 != null && frame2.Data.Length >= 4 && frame2.Data[3] != 0)
                                {
                                    frameData = frame2.Data;
                                    width = frame2.Width;
                                    height = frame2.Height;
                                }
                            }
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
                    // 스크럽 시 Preview만 업데이트 (타임라인은 마우스업에서 한 번만)
                    CurrentTimeMs = ts;
                    // NOTE: 타임라인 재그리기 제거 → 스크럽 성능 개선
                    // if (_timelineViewModel != null)
                    // {
                    //     _timelineViewModel.CurrentTimeMs = ts;
                    // }
                });

                // 재생 시작 시 취소
                if (_scrubCancelled) break;

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
    // 렌더 파이프라인 지연 측정용
    private long _renderDiagCount = 0;
    private long _renderDiagTotalMs = 0;

    private async Task PlaybackRenderAsync(long requestedTimeMs, bool useLookahead = true)
    {
        byte[]? frameData = null;
        uint width = 0, height = 0;
        // 비디오 디스플레이 지연 보상: 오디오보다 1프레임 앞서 요청
        // Dispatcher+vsync 지연(~22ms)을 상쇄하여 체감 A/V 동기화 개선
        // 초기 프레임에서는 lookahead를 사용하지 않음 (정확한 타임스탬프 확인용)
        long actualTimeMs = useLookahead ? requestedTimeMs + RenderLookaheadMs : requestedTimeMs;
        var pipelineSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    var taskRunStartMs = pipelineSw.ElapsedMilliseconds;

                    // PlaybackEngine에서 프레임 조회 (디코딩 없음, 즉시 반환)
                    // OnPlaybackTick에서 이미 오디오 클록 기반으로 계산한 requestedTimeMs를 그대로 사용
                    // Task.Run 스케줄링 지연이 있어도 큐 매칭 성공 (시간 일관성 유지)
                    using var frame = _projectService.TryGetPlaybackFrame(actualTimeMs);
                    if (frame != null)
                    {
                        // BLACK 프레임 안전장치: PlaybackEngine 큐에 잔류한 BLACK 프레임 무시
                        // fill_loop에서 필터링하지만 race condition으로 남을 수 있음
                        if (frame.Data.Length >= 4 && frame.Data[3] == 0)
                        {
                            if (requestedTimeMs == _playbackStartTimeMs)
                                Services.DebugLogger.Log($"[A/V SYNC] First frame BLACK (alpha=0) at {frame.TimestampMs}ms, skipping");
                            // frameData를 null로 유지 → 비트맵 업데이트 건너뜀 (이전 프레임 유지)
                        }
                        else
                        {
                            frameData = frame.Data;
                            width = frame.Width;
                            height = frame.Height;
                        }

                        // 진단: 1초 간격으로 30초간 드리프트 추적
                        var frameDelta = actualTimeMs - frame.TimestampMs;
                        if (_renderDiagCount < 900 && _renderDiagCount % 30 == 0)
                        {
                            var sec = (_renderDiagCount / 30) + 1;
                            var audioPos = _audioPlayback.GetPositionMs();
                            Services.DebugLogger.Log($"[DRIFT DIAG {sec}s] audioPos={audioPos}ms, frameTs={frame.TimestampMs}ms, delta={frameDelta}ms, elapsed={_playbackClock.ElapsedMilliseconds}ms");
                        }

                        if (requestedTimeMs == _playbackStartTimeMs && frameData != null)
                        {
                            var px0 = frameData.Length >= 4 ? $"{frameData[0]:X2}{frameData[1]:X2}{frameData[2]:X2}{frameData[3]:X2}" : "N/A";
                            Services.DebugLogger.Log($"[A/V SYNC] First frame from PlaybackEngine queue, actualTs={frame.TimestampMs}ms, px0={px0}");
                        }
                    }
                    else
                    {
                        // PlaybackEngine 없음 → fallback to RenderFrame
                        var audioPos = _audioPlayback.GetPositionMs(); // fallback 로그용으로만 조회
                        Services.DebugLogger.Log($"[QUEUE MISS] audioPos={audioPos}ms, requested={actualTimeMs}ms — fallback to RenderFrame");
                        if (requestedTimeMs == _playbackStartTimeMs)
                            Services.DebugLogger.Log($"[A/V SYNC] First frame FALLBACK to RenderFrame (queue empty!)");
                        using var fallbackFrame = _projectService.RenderFrame(actualTimeMs);
                        if (fallbackFrame != null)
                        {
                            frameData = fallbackFrame.Data;
                            width = fallbackFrame.Width;
                            height = fallbackFrame.Height;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.Log($"[PLAYBACK] Render error at {actualTimeMs}ms: {ex.Message}");
                }
            });

            var beforeUiMs = pipelineSw.ElapsedMilliseconds;

            // UI 스레드에서 비트맵만 업데이트
            if (frameData != null)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { UpdateBitmap(frameData, width, height); }
                    catch (Exception ex) { Services.DebugLogger.Log($"Bitmap error: {ex.Message}"); }
                });
            }

            var totalPipelineMs = pipelineSw.ElapsedMilliseconds;
            _renderDiagCount++;
            _renderDiagTotalMs += totalPipelineMs;

            // 파이프라인 소요시간 로그 (처음 5초간만)
            if (_renderDiagCount <= 150 && _renderDiagCount % 30 == 0)
            {
                var avgMs = _renderDiagTotalMs / Math.Max(1, _renderDiagCount);
                Services.DebugLogger.Log($"[PIPELINE #{_renderDiagCount}] total={totalPipelineMs}ms, avg={avgMs}ms");
            }
        }
        catch (Exception ex)
        {
            Services.DebugLogger.Log($"[PLAYBACK] Render error at {actualTimeMs}ms: {ex.Message}");
        }
        finally
        {
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
    public async Task TogglePlayback()
    {
        Services.DebugLogger.Log($"[PLAY] TogglePlayback: IsPlaying={IsPlaying}");

        if (IsPlaying)
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            _audioPlayback.Stop(); // 오디오 정지 (매번 새로 시작하여 동기화 보장)
            _projectService.StopPlaybackEngine(); // PlaybackEngine 정지
            _projectService.SetPlaybackMode(false); // 스크럽 모드로 전환
            IsPlaying = false;
            _scrubCancelled = false; // 스크럽 재개 허용
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
                // 스크럽 후 재생 시 타임라인 시간을 강제로 동기화하여 A/V 싱크 보장
                _timelineViewModel.CurrentTimeMs = CurrentTimeMs;
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
            _useA = true; // 더블 버퍼 상태 리셋 (스크럽↔재생 전환 시 꼬임 방지)
            _lastSyncLogMs = 0; _syncLogCounter = 0; // 동기화 진단 초기화
            _renderDiagCount = 0; _renderDiagTotalMs = 0;
            _lastTimelineUpdateMs = 0; // 타임라인 갱신 쓰로틀 리셋 (뒤로 스크럽 후 재생 시 갱신 안되는 버그 방지)

            // ── ScrubRenderLoop 취소 + Renderer Mutex 릴리스 대기 ──
            // ScrubRenderLoop이 원거리 seek 중이면 Renderer Mutex를 1.2~2초 보유
            // → 취소 플래그로 즉시 중단 요청 + 슬롯이 해제될 때까지 대기
            _scrubCancelled = true;
            Interlocked.Exchange(ref _pendingScrubTimeMs, -1); // pending 제거
            var scrubWaitStart = System.Diagnostics.Stopwatch.StartNew();
            while (Interlocked.CompareExchange(ref _scrubRenderActive, 0, 0) != 0)
            {
                await Task.Delay(10);
                if (scrubWaitStart.ElapsedMilliseconds > 5000)
                {
                    Services.DebugLogger.Log("[PLAY] ScrubRenderLoop wait timeout (5s), proceeding anyway");
                    break;
                }
            }
            if (scrubWaitStart.ElapsedMilliseconds > 0)
                Services.DebugLogger.Log($"[PLAY] ScrubRenderLoop stopped in {scrubWaitStart.ElapsedMilliseconds}ms");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── 병렬 초기화: 즉시 프리뷰 + PlaybackEngine warmup ──
            // ScrubRenderLoop이 이미 정지됨 → Renderer Mutex 확보 보장
            //
            // 1. PlaybackEngine warmup (별도 Renderer, 백그라운드)
            // 2. 메인 Renderer로 스크럽 프레임 1장 렌더 (즉시 표시)
            // 두 Renderer는 완전 독립 → 병렬 실행 시 Mutex 경합 없음
            var warmupTask = Task.Run(() => _projectService.StartPlaybackEngine(_playbackStartTimeMs));

            byte[]? initFrameData = null;
            uint initW = 0, initH = 0;
            await Task.Run(() =>
            {
                try
                {
                    using var frame = _projectService.RenderFrame(_playbackStartTimeMs);
                    if (frame != null)
                    {
                        initFrameData = frame.Data;
                        initW = frame.Width;
                        initH = frame.Height;

                        // FFmpeg 원거리 seek 아티팩트 검출:
                        // 키프레임 seek 후 첫 디코딩 프레임이 빈 프레임(alpha=0x00)일 수 있음
                        // 비디오 프레임의 alpha는 항상 0xFF → alpha=0x00이면 무효 프레임
                        if (initFrameData.Length >= 4 && initFrameData[3] == 0)
                        {
                            Services.DebugLogger.Log($"[PLAY] Init frame BLACK (alpha=0), retrying +33ms");
                            using var frame2 = _projectService.RenderFrame(_playbackStartTimeMs + 33);
                            if (frame2 != null && frame2.Data.Length >= 4 && frame2.Data[3] != 0)
                            {
                                initFrameData = frame2.Data;
                                initW = frame2.Width;
                                initH = frame2.Height;
                            }
                        }
                    }
                    else
                    {
                        Services.DebugLogger.Log($"[PLAY] Init frame null (Mutex busy?) at {_playbackStartTimeMs}ms");
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.Log($"[PLAY] Init frame render error: {ex.Message}");
                }
            });

            // 스크럽 프레임 즉시 표시 (warmup 대기 전에 화면 갱신)
            if (initFrameData != null)
            {
                UpdateBitmap(initFrameData, initW, initH);
                var initMs = sw.ElapsedMilliseconds;
                if (initFrameData.Length >= 4)
                {
                    var px0 = $"{initFrameData[0]:X2}{initFrameData[1]:X2}{initFrameData[2]:X2}{initFrameData[3]:X2}";
                    Services.DebugLogger.Log($"[A/V SYNC] Init frame at {_playbackStartTimeMs}ms rendered in {initMs}ms, px0={px0}");
                }
            }
            else
            {
                Services.DebugLogger.Log($"[A/V SYNC] Init frame FAILED — preview retains previous content until PlaybackEngine frame arrives");
            }

            // 재생 모드 전환 → 메인 렌더러의 디코더/캐시/last_rendered 전부 flush
            // Task.Run으로 실행: lock() 사용 시 UI 스레드 블로킹 방지
            await Task.Run(() => _projectService.SetPlaybackMode(true));

            // PlaybackEngine warmup 완료 대기
            await warmupTask;
            var playbackEngineReadyMs = sw.ElapsedMilliseconds;
            Services.DebugLogger.Log($"[A/V SYNC] PlaybackEngine ready at {playbackEngineReadyMs}ms (warmup complete)");

            // 첫 프레임 렌더링 (PlaybackEngine 큐에서 즉시 가져옴) - lookahead 없이 정확한 타임스탬프 확인
            long actualFirstFrameTimeMs = _playbackStartTimeMs;
            await PlaybackRenderAsync(_playbackStartTimeMs, useLookahead: false);
            var firstFrameRenderedMs = sw.ElapsedMilliseconds;
            Services.DebugLogger.Log($"[A/V SYNC] First frame rendered at {firstFrameRenderedMs}ms");

            // 실제 표시된 첫 프레임의 타임스탬프 확인 (BLACK 프레임이 아닐 경우)
            // lookahead 없이 렌더링했으므로 정확한 타임스탬프를 얻을 수 있음
            using var firstFrameCheck = _projectService.TryGetPlaybackFrame(_playbackStartTimeMs);
            if (firstFrameCheck != null && !(firstFrameCheck.Data.Length >= 4 && firstFrameCheck.Data[3] == 0))
            {
                actualFirstFrameTimeMs = firstFrameCheck.TimestampMs;
                Services.DebugLogger.Log($"[A/V SYNC] Using actual first frame timestamp: {actualFirstFrameTimeMs}ms (original: {_playbackStartTimeMs}ms)");
            }

            // 오디오 시작 (첫 프레임의 실제 타임스탬프로 동기화)
            if (Math.Abs(PlaybackSpeed - 1.0) < 0.01)
                _audioPlayback.Start(_projectService.TimelineRawHandle, actualFirstFrameTimeMs);
            else
                _audioPlayback.Stop();
            var audioStartedMs = sw.ElapsedMilliseconds;
            Services.DebugLogger.Log($"[A/V SYNC] Audio started at {audioStartedMs}ms with timestamp {actualFirstFrameTimeMs}ms");

            // 오디오 클록 마스터: cpal consumed_samples 기반으로 비디오 동기화
            // Stopwatch는 오디오 비활성 시(배속 재생) 폴백용으로만 사용
            _playbackClock.Restart();
            Services.DebugLogger.Log($"[A/V SYNC] Init complete, total elapsed={sw.ElapsedMilliseconds}ms");

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
    // A/V 동기화 진단: 1초마다 타이밍 비교 로그 출력
    private long _lastSyncLogMs = 0;
    private int _syncLogCounter = 0;

    private void OnPlaybackTick(object? sender, ElapsedEventArgs e)
    {
        // 양쪽 클록 동시 측정 (비교용)
        var audioPositionMs = _audioPlayback.GetPositionMs();
        var stopwatchElapsed = _playbackClock.ElapsedMilliseconds;
        var stopwatchTimeMs = _playbackStartTimeMs + (long)(stopwatchElapsed * PlaybackSpeed);

        long newTimeMs;
        if (audioPositionMs >= 0)
        {
            newTimeMs = audioPositionMs;
        }
        else
        {
            newTimeMs = stopwatchTimeMs;
        }

        // 1초마다 동기화 상태 로그 (처음 10초만)
        if (_syncLogCounter < 10 && (newTimeMs - _lastSyncLogMs) >= 1000)
        {
            _lastSyncLogMs = newTimeMs;
            _syncLogCounter++;
            var drift = audioPositionMs >= 0 ? (audioPositionMs - stopwatchTimeMs) : 0;
            Services.DebugLogger.Log($"[SYNC DIAG #{_syncLogCounter}] audioClock={audioPositionMs}ms, stopwatch={stopwatchTimeMs}ms, drift={drift}ms, newTimeMs={newTimeMs}ms");
        }

        // 클립 끝 감지: 캐시된 maxEndTime 사용 (매 틱 O(n) 반복 제거)
        if (newTimeMs >= _playbackMaxEndTimeMs && _playbackMaxEndTimeMs > 0)
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            _audioPlayback.Stop(); // 오디오 정지
            _projectService.StopPlaybackEngine(); // PlaybackEngine 정지
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
        PreviewSettings.PreviewFpsChanged -= OnPreviewFpsChanged;
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

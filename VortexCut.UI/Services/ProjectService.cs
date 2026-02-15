using VortexCut.Core.Interfaces;
using VortexCut.Core.Models;
using VortexCut.Interop.Services;

namespace VortexCut.UI.Services;

/// <summary>
/// 프로젝트 관리 서비스 (Rust Timeline/Renderer 연동)
/// </summary>
public class ProjectService : IProjectService
{
    private readonly TimelineService _timelineService;
    private readonly IRenderService _renderService;
    private Project? _currentProject;
    private ulong _defaultVideoTrackId;
    private TimelineHandle? _timelineHandle;

    public Project? CurrentProject => _currentProject;

    /// <summary>
    /// Rust Timeline의 원시 포인터 (Export용)
    /// </summary>
    public IntPtr TimelineRawHandle => _timelineHandle?.DangerousGetHandle() ?? IntPtr.Zero;

    // 직렬화 서비스용 내부 접근자
    internal TimelineService TimelineServiceInternal => _timelineService;
    internal IRenderService RenderServiceInternal => _renderService;
    internal Project? CurrentProjectInternal { get => _currentProject; set => _currentProject = value; }
    internal ulong DefaultVideoTrackIdInternal { get => _defaultVideoTrackId; set => _defaultVideoTrackId = value; }
    internal TimelineHandle? TimelineHandleInternal { get => _timelineHandle; set => _timelineHandle = value; }

    public ProjectService(IRenderService renderService, TimelineService timelineService)
    {
        _renderService = renderService;
        _timelineService = timelineService;
    }

    /// <summary>
    /// 새 프로젝트 생성
    /// </summary>
    public void CreateProject(string name, uint width = 1920, uint height = 1080, double fps = 30.0)
    {
        System.Diagnostics.Debug.WriteLine($"🎬 ProjectService.CreateProject START: {name}, {width}x{height}, {fps}fps");

        try
        {
            // 중요: 리소스 해제 순서
            // 1. Renderer 먼저 해제 (타임라인을 참조하고 있음)
            // 2. Timeline 해제
            System.Diagnostics.Debug.WriteLine("   [1/6] Destroying old renderer...");
            _renderService.DestroyRenderer();

            System.Diagnostics.Debug.WriteLine("   [2/6] Destroying old timeline...");
            _timelineService.DestroyTimeline();

            // 새 프로젝트 생성
            System.Diagnostics.Debug.WriteLine("   [3/6] Creating new project...");
            _currentProject = new Project(name, width, height, fps);

            System.Diagnostics.Debug.WriteLine("   [4/6] Creating timeline...");
            _timelineService.CreateTimeline(width, height, fps);

            // 기본 비디오 트랙 생성
            System.Diagnostics.Debug.WriteLine("   [5/6] Adding video track...");
            _defaultVideoTrackId = _timelineService.AddVideoTrack();
            System.Diagnostics.Debug.WriteLine($"       Default track ID: {_defaultVideoTrackId}");

            // Renderer 생성 (TimelineHandle 가져오기)
            System.Diagnostics.Debug.WriteLine("   [6/6] Creating renderer...");
            _timelineHandle = _timelineService.GetTimelineHandle();
            _renderService.CreateRenderer(_timelineHandle!.DangerousGetHandle());

            System.Diagnostics.Debug.WriteLine("   ✅ ProjectService.CreateProject COMPLETE");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"   ❌ ProjectService.CreateProject FAILED: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 비디오 클립 추가
    /// </summary>
    public ClipModel AddVideoClip(string filePath, long startTimeMs, long durationMs, int trackIndex = 0, string? proxyFilePath = null)
    {
        if (_currentProject == null)
            throw new InvalidOperationException("No project is open");

        System.Diagnostics.Debug.WriteLine($"📹 ProjectService.AddVideoClip: trackId={_defaultVideoTrackId}, filePath={filePath}");
        System.Diagnostics.Debug.WriteLine($"   startTimeMs={startTimeMs}, durationMs={durationMs}");

        var clipId = _timelineService.AddVideoClip(_defaultVideoTrackId, filePath, startTimeMs, durationMs);

        System.Diagnostics.Debug.WriteLine($"   ✅ Rust returned clipId={clipId}");

        // Timeline 상태 확인
        var videoTrackCount = _timelineService.GetVideoTrackCount();
        var audioTrackCount = _timelineService.GetAudioTrackCount();
        var clipCount = _timelineService.GetVideoClipCount(_defaultVideoTrackId);
        var duration = _timelineService.GetDuration();

        System.Diagnostics.Debug.WriteLine($"   📊 Timeline state: videoTracks={videoTrackCount}, audioTracks={audioTrackCount}, clipCount={clipCount}, duration={duration}ms");

        var clip = new ClipModel(clipId, filePath, startTimeMs, durationMs, trackIndex)
        {
            ProxyFilePath = proxyFilePath
        };
        _currentProject.Clips.Add(clip);

        return clip;
    }

    /// <summary>
    /// 비디오 클립 제거 (Undo용)
    /// Razor 분할로 생성된 클립은 Rust에 없을 수 있으므로 FFI 실패 시 무시
    /// </summary>
    public void RemoveVideoClip(ulong clipId, ulong trackId = 0)
    {
        if (_currentProject == null) return;

        var rustTrackId = trackId > 0 ? trackId : _defaultVideoTrackId;
        try { _timelineService.RemoveVideoClip(rustTrackId, clipId); }
        catch { /* Razor 분할 클립 등 Rust에 미등록 시 무시 */ }
        _currentProject.Clips.RemoveAll(c => c.Id == clipId);
    }

    /// <summary>
    /// 오디오 클립 제거 (Undo용)
    /// </summary>
    public void RemoveAudioClip(ulong clipId, ulong trackId)
    {
        if (_currentProject == null) return;

        try { _timelineService.RemoveAudioClip(trackId, clipId); }
        catch { /* Rust에 미등록 시 무시 */ }
        _currentProject.Clips.RemoveAll(c => c.Id == clipId);
    }

    /// <summary>
    /// 비디오 클립 재추가 (Redo/Undo용) — 새 Rust clipId 반환
    /// _currentProject.Clips에도 추가하여 정합성 유지
    /// </summary>
    public ulong ReAddVideoClip(string filePath, long startTimeMs, long durationMs)
    {
        if (_currentProject == null)
            throw new InvalidOperationException("No project is open");

        var newId = _timelineService.AddVideoClip(_defaultVideoTrackId, filePath, startTimeMs, durationMs);
        return newId;
    }

    /// <summary>
    /// 클립을 Rust Timeline에 동기화 (remove + re-add + trim 설정)
    /// 드래그/트림/Razor 후 C# 모델이 변경되었을 때 호출
    /// 새 Rust clipId로 clip.Id 갱신
    /// </summary>
    public void SyncClipToRust(ClipModel clip)
    {
        if (_currentProject == null) return;

        // _currentProject.Clips에서 기존 항목 제거 (ID로 찾기)
        _currentProject.Clips.RemoveAll(c => c.Id == clip.Id);

        // Rust에서 기존 클립 제거 (없으면 무시)
        try { _timelineService.RemoveVideoClip(_defaultVideoTrackId, clip.Id); }
        catch { }

        // Rust에 새 클립 추가
        var newId = _timelineService.AddVideoClip(
            _defaultVideoTrackId, clip.FilePath, clip.StartTimeMs, clip.DurationMs);
        clip.Id = newId;

        // trim_start_ms가 0이 아닌 경우 Rust에 설정
        if (clip.TrimStartMs > 0)
        {
            try
            {
                _timelineService.SetVideoClipTrim(
                    _defaultVideoTrackId, newId,
                    clip.TrimStartMs, clip.TrimStartMs + clip.DurationMs);
            }
            catch { }
        }

        // _currentProject.Clips에도 추가
        _currentProject.Clips.Add(clip);
    }

    /// <summary>
    /// 비디오 클립의 Rust trim 값 설정
    /// </summary>
    public void SetClipTrim(ulong clipId, long trimStartMs, long trimEndMs)
    {
        try
        {
            _timelineService.SetVideoClipTrim(_defaultVideoTrackId, clipId, trimStartMs, trimEndMs);
        }
        catch { }
    }

    /// <summary>
    /// 클립 볼륨 설정 (Inspector Audio 탭에서 호출)
    /// </summary>
    public void SetClipVolume(ulong clipId, float volume)
    {
        try { _timelineService.SetClipVolume(clipId, volume); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 클립 속도 설정 (Inspector Audio 탭에서 호출)
    /// </summary>
    public void SetClipSpeed(ulong clipId, double speed)
    {
        try { _timelineService.SetClipSpeed(clipId, speed); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 클립 페이드 설정 (Inspector Audio 탭에서 호출)
    /// </summary>
    public void SetClipFade(ulong clipId, long fadeInMs, long fadeOutMs)
    {
        try { _timelineService.SetClipFade(clipId, fadeInMs, fadeOutMs); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 클립 트랜지션 타입 설정 (Inspector Transition 탭에서 호출)
    /// </summary>
    public void SetClipTransition(ulong clipId, TransitionType type)
    {
        try { _timelineService.SetClipTransition(clipId, (uint)type); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 트랙 뮤트 설정 (TrackHeader M 버튼에서 호출)
    /// </summary>
    public void SetTrackMuted(ulong trackId, bool muted)
    {
        try { _timelineService.SetTrackMuted(trackId, muted); }
        catch { /* Timeline 미생성 시 무시 */ }
    }

    /// <summary>
    /// 클립 이펙트 설정 (Inspector Color 탭에서 호출)
    /// </summary>
    public void SetClipEffects(ulong clipId, float brightness, float contrast, float saturation, float temperature)
    {
        try { _renderService.SetClipEffects(clipId, brightness, contrast, saturation, temperature); }
        catch { /* Renderer 미생성 시 무시 */ }
    }

    /// <summary>
    /// 렌더 캐시 클리어 (Undo/Redo 후 호출)
    /// </summary>
    public void ClearRenderCache()
    {
        try { _renderService.ClearCache(); }
        catch { /* Renderer 미생성 시 무시 */ }
    }

    /// <summary>
    /// 특정 시간의 프레임 렌더링 (프레임 스킵 시 null 반환)
    /// </summary>
    public IRenderedFrame? RenderFrame(long timestampMs)
    {
        return _renderService.RenderFrame(timestampMs);
    }

    /// <summary>
    /// 비디오 파일 메타데이터 조회
    /// </summary>
    public VideoInfo GetVideoInfo(string filePath)
    {
        return _renderService.GetVideoInfo(filePath);
    }

    /// <summary>
    /// 비디오 썸네일 생성
    /// </summary>
    public IRenderedFrame GenerateThumbnail(string filePath, long timestampMs, uint thumbWidth, uint thumbHeight)
    {
        return _renderService.GenerateThumbnail(filePath, timestampMs, thumbWidth, thumbHeight);
    }

    /// <summary>
    /// 재생 모드 전환 (재생 시작 시 true, 정지 시 false)
    /// </summary>
    public void SetPlaybackMode(bool playback)
    {
        try { _renderService.SetPlaybackMode(playback); }
        catch { /* Renderer 미생성 시 무시 */ }
    }

    public void Dispose()
    {
        _renderService.Dispose();
        _timelineService.Dispose();
    }
}

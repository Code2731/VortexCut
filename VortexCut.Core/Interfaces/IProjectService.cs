using VortexCut.Core.Models;

namespace VortexCut.Core.Interfaces;

/// <summary>
/// 프로젝트 관리 서비스 인터페이스 (Rust Timeline/Renderer 연동)
/// </summary>
public interface IProjectService : IDisposable
{
    /// <summary>
    /// 현재 열린 프로젝트
    /// </summary>
    Project? CurrentProject { get; }

    /// <summary>
    /// Rust Timeline 원시 포인터 (Export용)
    /// </summary>
    IntPtr TimelineRawHandle { get; }

    /// <summary>
    /// 새 프로젝트 생성
    /// </summary>
    void CreateProject(string name, uint width = 1920, uint height = 1080, double fps = 30.0);

    /// <summary>
    /// 비디오 클립 추가
    /// </summary>
    ClipModel AddVideoClip(string filePath, long startTimeMs, long durationMs, int trackIndex = 0, string? proxyFilePath = null);

    /// <summary>
    /// 비디오 클립 제거
    /// </summary>
    void RemoveVideoClip(ulong clipId, ulong trackId = 0);

    /// <summary>
    /// 오디오 클립 제거
    /// </summary>
    void RemoveAudioClip(ulong clipId, ulong trackId);

    /// <summary>
    /// 비디오 클립 재추가 (Redo/Undo용)
    /// </summary>
    ulong ReAddVideoClip(string filePath, long startTimeMs, long durationMs);

    /// <summary>
    /// 클립을 Rust Timeline에 동기화 (remove + re-add)
    /// </summary>
    void SyncClipToRust(ClipModel clip);

    /// <summary>
    /// 비디오 클립 Rust trim 값 설정
    /// </summary>
    void SetClipTrim(ulong clipId, long trimStartMs, long trimEndMs);

    /// <summary>
    /// 클립 볼륨 설정
    /// </summary>
    void SetClipVolume(ulong clipId, float volume);

    /// <summary>
    /// 클립 속도 설정
    /// </summary>
    void SetClipSpeed(ulong clipId, double speed);

    /// <summary>
    /// 클립 페이드 설정
    /// </summary>
    void SetClipFade(ulong clipId, long fadeInMs, long fadeOutMs);

    /// <summary>
    /// 클립 트랜지션 타입 설정
    /// </summary>
    void SetClipTransition(ulong clipId, TransitionType type);

    /// <summary>
    /// 클립 이펙트 설정 (Brightness, Contrast, Saturation, Temperature)
    /// </summary>
    void SetClipEffects(ulong clipId, float brightness, float contrast, float saturation, float temperature);

    /// <summary>
    /// 렌더 캐시 클리어
    /// </summary>
    void ClearRenderCache();

    /// <summary>
    /// 재생 모드 전환 (재생 시작 시 true, 정지 시 false)
    /// </summary>
    void SetPlaybackMode(bool playback);

    // === 렌더링 ===

    /// <summary>
    /// 프레임 렌더링 (Mutex busy 시 null 반환 = 프레임 스킵)
    /// </summary>
    IRenderedFrame? RenderFrame(long timestampMs);

    /// <summary>
    /// 비디오 파일 메타데이터 조회
    /// </summary>
    VideoInfo GetVideoInfo(string filePath);

    /// <summary>
    /// 비디오 썸네일 생성
    /// </summary>
    IRenderedFrame GenerateThumbnail(string filePath, long timestampMs, uint thumbWidth, uint thumbHeight);
}

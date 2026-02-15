using VortexCut.Core.Models;

namespace VortexCut.Core.Interfaces;

/// <summary>
/// 렌더링 서비스 인터페이스 (Rust Renderer 연동)
/// </summary>
public interface IRenderService : IDisposable
{
    // === 렌더러 생명주기 ===

    /// <summary>
    /// Renderer 생성 (Timeline 핸들 필요)
    /// </summary>
    void CreateRenderer(IntPtr timelineHandle);

    /// <summary>
    /// Renderer 명시적 해제
    /// </summary>
    void DestroyRenderer();

    // === 프레임 렌더링 ===

    /// <summary>
    /// 프레임 렌더링 (Mutex busy 시 null 반환 = 프레임 스킵)
    /// </summary>
    IRenderedFrame? RenderFrame(long timestampMs);

    // === 모드/이펙트 ===

    /// <summary>
    /// 재생 모드 전환 (재생=true: forward decode, 스크럽=false: 즉시 seek)
    /// </summary>
    void SetPlaybackMode(bool playback);

    /// <summary>
    /// 클립 이펙트 설정 (값 범위: -1.0 ~ 1.0, 0=원본)
    /// </summary>
    void SetClipEffects(ulong clipId, float brightness, float contrast, float saturation, float temperature);

    // === 캐시 ===

    /// <summary>
    /// 프레임 캐시 클리어
    /// </summary>
    void ClearCache();

    /// <summary>
    /// 캐시 통계 조회
    /// </summary>
    (uint CachedFrames, nuint CacheBytes) GetCacheStats();

    // === 유틸리티 ===

    /// <summary>
    /// 비디오 파일 메타데이터 조회
    /// </summary>
    VideoInfo GetVideoInfo(string filePath);

    /// <summary>
    /// 비디오 썸네일 생성
    /// </summary>
    IRenderedFrame GenerateThumbnail(string filePath, long timestampMs, uint thumbWidth, uint thumbHeight);
}

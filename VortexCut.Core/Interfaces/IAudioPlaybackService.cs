namespace VortexCut.Core.Interfaces;

/// <summary>
/// 실시간 오디오 재생 서비스 인터페이스 (Rust cpal 엔진)
/// </summary>
public interface IAudioPlaybackService : IDisposable
{
    /// <summary>
    /// 오디오 재생 중인지 여부
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// 오디오 재생 시작
    /// </summary>
    /// <param name="timelineHandle">Rust Timeline의 Arc 포인터</param>
    /// <param name="startTimeMs">재생 시작 위치 (타임라인 시간)</param>
    void Start(IntPtr timelineHandle, long startTimeMs);

    /// <summary>
    /// 오디오 재생 정지
    /// </summary>
    void Stop();

    /// <summary>
    /// 오디오 일시정지
    /// </summary>
    void Pause();

    /// <summary>
    /// 오디오 재개
    /// </summary>
    void Resume();

    /// <summary>
    /// 현재 오디오 재생 위치 (ms) — cpal 출력 샘플 기준
    /// 비활성 시 -1 반환
    /// </summary>
    long GetPositionMs();
}

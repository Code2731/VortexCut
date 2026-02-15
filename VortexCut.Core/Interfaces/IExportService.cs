namespace VortexCut.Core.Interfaces;

/// <summary>
/// Export 서비스 인터페이스 — 타임라인을 MP4로 내보내기
/// </summary>
public interface IExportService : IDisposable
{
    /// <summary>
    /// Export 시작 (기본)
    /// </summary>
    void StartExport(IntPtr timelineHandle, string outputPath, uint width, uint height, double fps, uint crf);

    /// <summary>
    /// 자막 포함 Export 시작 (v2)
    /// </summary>
    void StartExportWithSubtitles(IntPtr timelineHandle, string outputPath, uint width, uint height, double fps, uint crf, IntPtr subtitleListHandle);

    /// <summary>
    /// 인코더 타입 선택 + 자막 포함 Export 시작 (v3)
    /// encoderType: 0=Auto, 1=Software, 2=NVENC, 3=QSV, 4=AMF
    /// </summary>
    void StartExportV3(IntPtr timelineHandle, string outputPath, uint width, uint height, double fps, uint crf, uint encoderType, IntPtr subtitleListHandle);

    /// <summary>
    /// Export 진행률 (0~100)
    /// </summary>
    int GetProgress();

    /// <summary>
    /// Export 완료 여부
    /// </summary>
    bool IsFinished();

    /// <summary>
    /// Export 에러 메시지 (null이면 성공 또는 진행 중)
    /// </summary>
    string? GetError();

    /// <summary>
    /// Export 취소
    /// </summary>
    void Cancel();

    /// <summary>
    /// Export 작업 정리 (완료/취소 후 호출)
    /// </summary>
    void Cleanup();

    /// <summary>
    /// 자막 오버레이 리스트 생성 (Rust 핸들 반환)
    /// </summary>
    IntPtr CreateSubtitleList();

    /// <summary>
    /// 자막 오버레이 리스트에 항목 추가
    /// </summary>
    void SubtitleListAdd(IntPtr listHandle, long startMs, long endMs, int x, int y, uint width, uint height, IntPtr rgbaPtr, uint rgbaLen);
}

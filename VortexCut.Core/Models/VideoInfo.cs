namespace VortexCut.Core.Models;

/// <summary>
/// 비디오 파일 메타데이터
/// </summary>
public record VideoInfo(long DurationMs, uint Width, uint Height, double Fps);

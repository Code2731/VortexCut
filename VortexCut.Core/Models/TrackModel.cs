namespace VortexCut.Core.Models;

/// <summary>
/// 트랙 타입 (비디오/오디오)
/// </summary>
public enum TrackType
{
    Video,
    Audio
}

/// <summary>
/// 타임라인 트랙 모델
/// </summary>
public class TrackModel
{
    /// <summary>
    /// 트랙 고유 ID (Rust Timeline의 트랙 ID와 매핑)
    /// </summary>
    public ulong Id { get; set; }

    /// <summary>
    /// 트랙 인덱스 (0부터 시작, 표시 순서)
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 트랙 타입 (비디오/오디오)
    /// </summary>
    public TrackType Type { get; set; }

    /// <summary>
    /// 트랙 이름 (사용자가 편집 가능)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 트랙 활성화 상태
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 뮤트 상태 (오디오 비활성화)
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// 솔로 상태 (이 트랙만 재생, 다른 트랙 뮤트)
    /// </summary>
    public bool IsSolo { get; set; }

    /// <summary>
    /// 잠금 상태 (편집 불가)
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// 트랙 색상 라벨 (ARGB 형식: 0xAARRGGBB)
    /// </summary>
    public uint ColorArgb { get; set; } = 0xFF808080; // Gray

    /// <summary>
    /// 트랙 높이 (픽셀 단위, 30~200px)
    /// </summary>
    public double Height { get; set; } = 60;
}

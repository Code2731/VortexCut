using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace VortexCut.Core.Models;

/// <summary>
/// 트랙 타입 (비디오/오디오/자막)
/// </summary>
public enum TrackType
{
    Video,
    Audio,
    Subtitle
}

/// <summary>
/// 클립 표시 모드 (DaVinci Resolve / Premiere Pro 스타일)
/// </summary>
public enum ClipDisplayMode
{
    /// <summary>연속 프레임 썸네일 (기본값)</summary>
    Filmstrip,
    /// <summary>시작/끝 프레임만 표시</summary>
    Thumbnail,
    /// <summary>클립 이름 + 단색 (최소)</summary>
    Minimal
}

/// <summary>
/// 오디오 파형 표시 모드 (Premiere Pro 참조)
/// </summary>
public enum WaveformDisplayMode
{
    /// <summary>반파 - 아래→위 방향 (Premiere 기본)</summary>
    Rectified,
    /// <summary>전파 - 중앙 기준 상하 대칭</summary>
    NonRectified,
    /// <summary>숨김</summary>
    Hidden
}

/// <summary>
/// 타임라인 트랙 모델
/// </summary>
public class TrackModel
{
    [Category("기본 정보")]
    [DisplayName("트랙 ID")]
    [ReadOnly(true)]
    public ulong Id { get; set; }

    [Category("기본 정보")]
    [DisplayName("인덱스")]
    [ReadOnly(true)]
    public int Index { get; set; }

    [Category("기본 정보")]
    [DisplayName("타입")]
    [ReadOnly(true)]
    public TrackType Type { get; set; }

    [Category("트랙")]
    [DisplayName("트랙 이름")]
    public string Name { get; set; } = string.Empty;

    [Category("상태")]
    [DisplayName("활성화")]
    public bool IsEnabled { get; set; } = true;

    [Category("상태")]
    [DisplayName("뮤트")]
    public bool IsMuted { get; set; }

    [Category("상태")]
    [DisplayName("솔로")]
    public bool IsSolo { get; set; }

    [Category("상태")]
    [DisplayName("잠금")]
    public bool IsLocked { get; set; }

    /// <summary>
    /// 트랙 Armed 상태 (3-Point 편집 대상 트랙)
    /// 같은 타입 중 1개만 armed 가능
    /// </summary>
    [Category("상태")]
    [DisplayName("Armed")]
    public bool IsArmed { get; set; }

    [Category("표시")]
    [DisplayName("색상 (ARGB)")]
    public uint ColorArgb { get; set; } = 0xFF808080; // Gray

    [Category("표시")]
    [DisplayName("트랙 높이")]
    [Range(30, 200)]
    public double Height { get; set; } = 60;

    [Category("표시")]
    [DisplayName("표시 모드")]
    public ClipDisplayMode DisplayMode { get; set; } = ClipDisplayMode.Filmstrip;
}

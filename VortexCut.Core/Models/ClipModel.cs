using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace VortexCut.Core.Models;

/// <summary>
/// 타임라인 클립 모델
/// </summary>
public class ClipModel
{
    [Category("기본 정보")]
    [DisplayName("클립 ID")]
    [ReadOnly(true)]
    public ulong Id { get; set; }

    [Category("기본 정보")]
    [DisplayName("파일 경로")]
    [ReadOnly(true)]
    public string FilePath { get; set; } = string.Empty;

    [Category("타임라인")]
    [DisplayName("시작 시간 (ms)")]
    [Range(0, long.MaxValue)]
    public long StartTimeMs { get; set; }

    [Category("타임라인")]
    [DisplayName("길이 (ms)")]
    [Range(1, long.MaxValue)]
    public long DurationMs { get; set; }

    [Category("타임라인")]
    [DisplayName("트랙 번호")]
    public int TrackIndex { get; set; }

    /// <summary>
    /// 프리뷰/Filmstrip용 Proxy 비디오 경로 (있으면 우선 사용)
    /// </summary>
    [Browsable(false)]
    public string? ProxyFilePath { get; set; }

    // 클립 색상 라벨 (DaVinci Resolve 스타일)
    [Category("표시")]
    [DisplayName("색상 라벨 (ARGB)")]
    public uint ColorLabelArgb { get; set; } = 0; // 0 = 색상 없음

    // 링크된 클립 (비디오+오디오 연결)
    [Browsable(false)]
    public ulong? LinkedAudioClipId { get; set; }
    [Browsable(false)]
    public ulong? LinkedVideoClipId { get; set; }

    /// <summary>
    /// 다른 클립과 링크되어 있는지 여부
    /// </summary>
    [Category("기본 정보")]
    [DisplayName("링크됨")]
    [ReadOnly(true)]
    public bool IsLinked => LinkedAudioClipId.HasValue || LinkedVideoClipId.HasValue;

    // 키프레임 시스템 (After Effects 스타일) — 별도 키프레임 에디터에서 편집
    [Browsable(false)]
    public KeyframeSystem OpacityKeyframes { get; } = new KeyframeSystem();
    [Browsable(false)]
    public KeyframeSystem VolumeKeyframes { get; } = new KeyframeSystem();
    [Browsable(false)]
    public KeyframeSystem PositionXKeyframes { get; } = new KeyframeSystem();
    [Browsable(false)]
    public KeyframeSystem PositionYKeyframes { get; } = new KeyframeSystem();
    [Browsable(false)]
    public KeyframeSystem ScaleKeyframes { get; } = new KeyframeSystem();
    [Browsable(false)]
    public KeyframeSystem RotationKeyframes { get; } = new KeyframeSystem();

    /// <summary>
    /// 애니메이션이 활성화되어 있는지 여부 (키프레임 존재 여부)
    /// </summary>
    [Category("기본 정보")]
    [DisplayName("애니메이션")]
    [ReadOnly(true)]
    public bool HasAnimation =>
        OpacityKeyframes.Keyframes.Count > 0 ||
        VolumeKeyframes.Keyframes.Count > 0 ||
        PositionXKeyframes.Keyframes.Count > 0 ||
        PositionYKeyframes.Keyframes.Count > 0 ||
        ScaleKeyframes.Keyframes.Count > 0 ||
        RotationKeyframes.Keyframes.Count > 0;

    public ClipModel() { }

    public ClipModel(ulong id, string filePath, long startTimeMs, long durationMs, int trackIndex = 0)
    {
        Id = id;
        FilePath = filePath;
        StartTimeMs = startTimeMs;
        DurationMs = durationMs;
        TrackIndex = trackIndex;
    }

    [Category("타임라인")]
    [DisplayName("종료 시간 (ms)")]
    [ReadOnly(true)]
    public long EndTimeMs => StartTimeMs + DurationMs;
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace VortexCut.Core.Models;

/// <summary>
/// 비디오 트랜지션 타입 (incoming 클립에 설정)
/// </summary>
public enum TransitionType
{
    None = 0,
    Crossfade = 1,
    FadeBlack = 2,
    WipeLeft = 3,
    WipeRight = 4,
    WipeUp = 5,
    WipeDown = 6
}

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

    /// <summary>
    /// 원본 파일에서의 트림 시작 위치 (ms). Razor 분할 시 설정.
    /// 예: 10초 영상을 5초에서 분할 → 우측 클립의 TrimStartMs = 5000
    /// </summary>
    [Browsable(false)]
    public long TrimStartMs { get; set; }

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

    // 색보정 이펙트 (-1.0 ~ 1.0, 0=원본)
    [Category("이펙트")]
    [DisplayName("밝기")]
    public double Brightness { get; set; } = 0.0;

    [Category("이펙트")]
    [DisplayName("대비")]
    public double Contrast { get; set; } = 0.0;

    [Category("이펙트")]
    [DisplayName("채도")]
    public double Saturation { get; set; } = 0.0;

    [Category("이펙트")]
    [DisplayName("색온도")]
    public double Temperature { get; set; } = 0.0;

    // 오디오/속도 설정
    [Category("오디오")]
    [DisplayName("볼륨")]
    public double Volume { get; set; } = 1.0;  // 0.0~2.0

    [Category("오디오")]
    [DisplayName("속도")]
    public double Speed { get; set; } = 1.0;   // 0.25~4.0

    [Category("오디오")]
    [DisplayName("페이드 인 (ms)")]
    public long FadeInMs { get; set; } = 0;

    [Category("오디오")]
    [DisplayName("페이드 아웃 (ms)")]
    public long FadeOutMs { get; set; } = 0;

    // 트랜지션 (Phase 11)
    [Category("트랜지션")]
    [DisplayName("트랜지션 타입")]
    public TransitionType TransitionType { get; set; } = TransitionType.None;

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

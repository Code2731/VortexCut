using System.Text.Json;
using System.Text.Json.Serialization;
using VortexCut.Core.Models;

namespace VortexCut.Core.Serialization;

/// <summary>
/// JSON 직렬화용 최상위 프로젝트 DTO.
/// </summary>
public class ProjectData
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "Untitled";

    [JsonPropertyName("width")]
    public uint Width { get; set; } = 1920;

    [JsonPropertyName("height")]
    public uint Height { get; set; } = 1080;

    [JsonPropertyName("fps")]
    public double Fps { get; set; } = 30.0;

    [JsonPropertyName("snapEnabled")]
    public bool SnapEnabled { get; set; } = true;

    [JsonPropertyName("snapThresholdMs")]
    public long SnapThresholdMs { get; set; } = 100;

    [JsonPropertyName("inPointMs")]
    public long? InPointMs { get; set; }

    [JsonPropertyName("outPointMs")]
    public long? OutPointMs { get; set; }

    [JsonPropertyName("mediaItems")]
    public List<MediaItemData> MediaItems { get; set; } = new();

    [JsonPropertyName("videoTracks")]
    public List<TrackData> VideoTracks { get; set; } = new();

    [JsonPropertyName("audioTracks")]
    public List<TrackData> AudioTracks { get; set; } = new();

    [JsonPropertyName("clips")]
    public List<ClipData> Clips { get; set; } = new();

    [JsonPropertyName("subtitleTracks")]
    public List<TrackData> SubtitleTracks { get; set; } = new();

    [JsonPropertyName("markers")]
    public List<MarkerData> Markers { get; set; } = new();
}

/// <summary>
/// Project Bin 미디어 아이템 DTO.
/// (썸네일 경로/ImportedAt 등은 저장하지 않는다)
/// </summary>
public class MediaItemData
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public MediaType Type { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("width")]
    public uint Width { get; set; }

    [JsonPropertyName("height")]
    public uint Height { get; set; }

    [JsonPropertyName("fps")]
    public double Fps { get; set; }

    [JsonPropertyName("proxyFilePath")]
    public string? ProxyFilePath { get; set; }
}

/// <summary>
/// 타임라인 트랙 DTO.
/// </summary>
public class TrackData
{
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; set; }

    [JsonPropertyName("isSolo")]
    public bool IsSolo { get; set; }

    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }

    [JsonPropertyName("colorArgb")]
    public uint ColorArgb { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

/// <summary>
/// 타임라인 클립 DTO.
/// </summary>
public class ClipData
{
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("startTimeMs")]
    public long StartTimeMs { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>
    /// 비디오/오디오 트랙을 합친 전체 인덱스.
    /// (0..videoCount-1 = 비디오, 이후는 오디오)
    /// </summary>
    [JsonPropertyName("trackIndex")]
    public int TrackIndex { get; set; }

    [JsonPropertyName("colorLabelArgb")]
    public uint ColorLabelArgb { get; set; }

    [JsonPropertyName("linkedAudioClipId")]
    public ulong? LinkedAudioClipId { get; set; }

    [JsonPropertyName("linkedVideoClipId")]
    public ulong? LinkedVideoClipId { get; set; }

    /// <summary>자막 클립 텍스트 (SubtitleClipModel일 때만)</summary>
    [JsonPropertyName("subtitleText")]
    public string? SubtitleText { get; set; }

    /// <summary>자막 스타일 (SubtitleClipModel일 때만)</summary>
    [JsonPropertyName("subtitleStyle")]
    public SubtitleStyleData? SubtitleStyle { get; set; }

    // 색보정 이펙트 (-1.0 ~ 1.0, 0=원본)
    [JsonPropertyName("brightness")]
    public double Brightness { get; set; }

    [JsonPropertyName("contrast")]
    public double Contrast { get; set; }

    [JsonPropertyName("saturation")]
    public double Saturation { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    // 오디오/속도 설정
    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1.0;

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;

    [JsonPropertyName("fadeInMs")]
    public long FadeInMs { get; set; }

    [JsonPropertyName("fadeOutMs")]
    public long FadeOutMs { get; set; }

    [JsonPropertyName("opacityKeyframes")]
    public KeyframeSystemData OpacityKeyframes { get; set; } = new();

    [JsonPropertyName("volumeKeyframes")]
    public KeyframeSystemData VolumeKeyframes { get; set; } = new();

    [JsonPropertyName("positionXKeyframes")]
    public KeyframeSystemData PositionXKeyframes { get; set; } = new();

    [JsonPropertyName("positionYKeyframes")]
    public KeyframeSystemData PositionYKeyframes { get; set; } = new();

    [JsonPropertyName("scaleKeyframes")]
    public KeyframeSystemData ScaleKeyframes { get; set; } = new();

    [JsonPropertyName("rotationKeyframes")]
    public KeyframeSystemData RotationKeyframes { get; set; } = new();
}

/// <summary>
/// 타임라인 마커 DTO.
/// </summary>
public class MarkerData
{
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    [JsonPropertyName("timeMs")]
    public long TimeMs { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("colorArgb")]
    public uint ColorArgb { get; set; } = 0xFFFFFF00;

    [JsonPropertyName("type")]
    public MarkerType Type { get; set; } = MarkerType.Comment;

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }
}

/// <summary>
/// 키프레임 시스템 DTO.
/// </summary>
public class KeyframeSystemData
{
    [JsonPropertyName("keyframes")]
    public List<KeyframeData> Keyframes { get; set; } = new();
}

/// <summary>
/// 단일 키프레임 DTO.
/// </summary>
public class KeyframeData
{
    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("interpolation")]
    public InterpolationType Interpolation { get; set; } = InterpolationType.Linear;

    [JsonPropertyName("inHandle")]
    public BezierHandleData? InHandle { get; set; }

    [JsonPropertyName("outHandle")]
    public BezierHandleData? OutHandle { get; set; }
}

/// <summary>
/// 베지어 핸들 DTO.
/// </summary>
public class BezierHandleData
{
    [JsonPropertyName("timeOffset")]
    public double TimeOffset { get; set; }

    [JsonPropertyName("valueOffset")]
    public double ValueOffset { get; set; }
}

/// <summary>
/// 자막 스타일 DTO.
/// </summary>
public class SubtitleStyleData
{
    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; set; } = "Arial";

    [JsonPropertyName("fontSize")]
    public double FontSize { get; set; } = 48;

    [JsonPropertyName("fontColorArgb")]
    public uint FontColorArgb { get; set; } = 0xFFFFFFFF;

    [JsonPropertyName("outlineColorArgb")]
    public uint OutlineColorArgb { get; set; } = 0xFF000000;

    [JsonPropertyName("outlineThickness")]
    public double OutlineThickness { get; set; } = 2.0;

    [JsonPropertyName("backgroundColorArgb")]
    public uint BackgroundColorArgb { get; set; } = 0x80000000;

    [JsonPropertyName("position")]
    public SubtitlePosition Position { get; set; } = SubtitlePosition.Bottom;

    [JsonPropertyName("isBold")]
    public bool IsBold { get; set; }

    [JsonPropertyName("isItalic")]
    public bool IsItalic { get; set; }
}

/// <summary>
/// ProjectData JSON 직렬화/역직렬화 도우미.
/// </summary>
public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(ProjectData project)
        => JsonSerializer.Serialize(project, Options);

    public static ProjectData? Deserialize(string json)
        => JsonSerializer.Deserialize<ProjectData>(json, Options);

    public static async Task SaveToFileAsync(ProjectData project, string filePath)
    {
        var json = Serialize(project);
        await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8);
    }

    public static async Task<ProjectData?> LoadFromFileAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8);
        return Deserialize(json);
    }
}


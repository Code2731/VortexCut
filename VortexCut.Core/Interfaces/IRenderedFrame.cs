namespace VortexCut.Core.Interfaces;

/// <summary>
/// 렌더링된 프레임 데이터 계약 (RGBA 픽셀 데이터)
/// </summary>
public interface IRenderedFrame : IDisposable
{
    /// <summary>프레임 폭 (px)</summary>
    uint Width { get; }

    /// <summary>프레임 높이 (px)</summary>
    uint Height { get; }

    /// <summary>RGBA 픽셀 데이터</summary>
    byte[] Data { get; }

    /// <summary>타임스탬프 (ms)</summary>
    long TimestampMs { get; }
}

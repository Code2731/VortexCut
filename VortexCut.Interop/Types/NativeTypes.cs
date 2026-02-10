using System.Runtime.InteropServices;

namespace VortexCut.Interop.Types;

/// <summary>
/// 에러 코드 상수
/// </summary>
public static class ErrorCodes
{
    public const int SUCCESS = 0;
    public const int NULL_PTR = 1;
    public const int INVALID_PARAM = 2;
    public const int FFMPEG = 3;
    public const int IO = 4;
    public const int RENDER_FAILED = 5;
    public const int UNKNOWN = 99;
}

/// <summary>
/// C-compatible 에러 구조체
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeError
{
    public int Code;
    public IntPtr Message;
}

/// <summary>
/// C-compatible 클립 구조체
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeClip
{
    public ulong Id;
    public long StartTimeMs;
    public long DurationMs;
    public int TrackIndex;
    public int ClipType;  // 0=Video, 1=Audio, 2=Image
    public IntPtr FilePath;
}

/// <summary>
/// C-compatible 렌더 프레임 구조체
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeRenderFrame
{
    public uint Width;
    public uint Height;
    public int Format;  // 0=RGBA, 1=RGB, 2=YUV420P
    public IntPtr Data;
    public nuint DataLen;
}

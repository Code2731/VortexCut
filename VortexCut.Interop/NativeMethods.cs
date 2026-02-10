using System.Runtime.InteropServices;

namespace VortexCut.Interop;

/// <summary>
/// Rust 네이티브 라이브러리 P/Invoke 선언
/// </summary>
public static class NativeMethods
{
    // 크로스 플랫폼 라이브러리 이름
    // Windows: rust_engine.dll
    // macOS/Linux: librust_engine.dylib/so (lib 접두사 필요)
#if WINDOWS
    private const string DllName = "rust_engine";
#else
    private const string DllName = "librust_engine";
#endif

    /// <summary>
    /// Rust에서 할당한 문자열 메모리 해제
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void string_free(IntPtr ptr);

    /// <summary>
    /// Hello World 테스트 함수
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hello_world();

    /// <summary>
    /// 두 수를 더하는 테스트 함수
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_numbers(int a, int b);

    // ==================== Timeline Functions ====================

    /// <summary>
    /// Timeline 생성
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_create(uint width, uint height, double fps, out IntPtr outTimeline);

    /// <summary>
    /// Timeline 파괴
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_destroy(IntPtr timeline);

    /// <summary>
    /// 비디오 트랙 추가
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_add_video_track(IntPtr timeline, out ulong outTrackId);

    /// <summary>
    /// 오디오 트랙 추가
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_add_audio_track(IntPtr timeline, out ulong outTrackId);

    /// <summary>
    /// 비디오 클립 추가
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_add_video_clip(
        IntPtr timeline,
        ulong trackId,
        IntPtr filePath,
        long startTimeMs,
        long durationMs,
        out ulong outClipId);

    /// <summary>
    /// 오디오 클립 추가
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_add_audio_clip(
        IntPtr timeline,
        ulong trackId,
        IntPtr filePath,
        long startTimeMs,
        long durationMs,
        out ulong outClipId);

    /// <summary>
    /// 비디오 클립 제거
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_remove_video_clip(IntPtr timeline, ulong trackId, ulong clipId);

    /// <summary>
    /// 오디오 클립 제거
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_remove_audio_clip(IntPtr timeline, ulong trackId, ulong clipId);

    /// <summary>
    /// 타임라인 총 길이 가져오기
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_get_duration(IntPtr timeline, out long outDurationMs);

    /// <summary>
    /// 비디오 트랙 개수 가져오기
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_get_video_track_count(IntPtr timeline, out nuint outCount);

    /// <summary>
    /// 오디오 트랙 개수 가져오기
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_get_audio_track_count(IntPtr timeline, out nuint outCount);

    // ==================== Renderer Functions ====================

    /// <summary>
    /// Renderer 생성
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_create(IntPtr timeline, out IntPtr outRenderer);

    /// <summary>
    /// Renderer 파괴
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_destroy(IntPtr renderer);

    /// <summary>
    /// 프레임 렌더링
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_render_frame(
        IntPtr renderer,
        long timestampMs,
        out uint outWidth,
        out uint outHeight,
        out IntPtr outData,
        out nuint outDataSize);

    /// <summary>
    /// 렌더링된 프레임 데이터 해제
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_free_frame_data(IntPtr data, nuint size);
}

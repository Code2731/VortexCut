using System;
using System.Runtime.InteropServices;

namespace VortexCut.UI.Services;

/// <summary>
/// C-compatible 세그먼트 구조체 (Rust FFI 대응)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CTranscriptSegment
{
    public long StartMs;
    public long EndMs;
    /// <summary>UTF-8 문자열 포인터 (null-terminated)</summary>
    public IntPtr TextPtr;
    public uint TextLen;
}

/// <summary>
/// Whisper FFI P/Invoke 선언
/// </summary>
internal static class WhisperInterop
{
    private const string DllName = "rust_engine";

    /// <summary>
    /// 트랜스크립션 시작 — 백그라운드 스레드에서 실행
    /// </summary>
    /// <returns>0=성공</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcriber_start(
        IntPtr audioPath,
        IntPtr modelPath,
        IntPtr language,
        out IntPtr outJob);

    /// <summary>
    /// 진행률 가져오기 (0~100)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint transcriber_get_progress(IntPtr job);

    /// <summary>
    /// 완료 여부 (1=완료, 0=진행중)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcriber_is_finished(IntPtr job);

    /// <summary>
    /// 에러 메시지 가져오기 (없으면 outError=null)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcriber_get_error(IntPtr job, out IntPtr outError);

    /// <summary>
    /// 세그먼트 목록 가져오기
    /// </summary>
    /// <returns>CTranscriptSegment 배열 포인터 (transcriber_free_segments로 해제)</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr transcriber_get_segments(IntPtr job, out uint outCount);

    /// <summary>
    /// 세그먼트 배열 해제
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcriber_free_segments(IntPtr ptr, uint count);

    /// <summary>
    /// 에러 문자열 해제
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcriber_free_string(IntPtr ptr);

    /// <summary>
    /// 중단 요청 — 다음 세그먼트 경계에서 Whisper 처리 중단
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcriber_request_abort(IntPtr job);

    /// <summary>
    /// TranscriberJob 파괴 — 반드시 호출 필요
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcriber_destroy(IntPtr job);
}

using System.Runtime.InteropServices;
using VortexCut.Interop.Types;

namespace VortexCut.Interop.Services;

/// <summary>
/// Export 서비스 - Rust exporter FFI 래퍼
/// 타임라인을 MP4 파일로 내보내기 (백그라운드 스레드)
/// </summary>
public class ExportService : IDisposable
{
    private IntPtr _jobHandle = IntPtr.Zero;
    private bool _disposed;

    /// <summary>
    /// Export 시작 (Rust 백그라운드 스레드에서 실행)
    /// </summary>
    /// <param name="timelineHandle">Rust Timeline의 Arc 포인터</param>
    /// <param name="outputPath">MP4 출력 파일 경로</param>
    /// <param name="width">출력 해상도 너비</param>
    /// <param name="height">출력 해상도 높이</param>
    /// <param name="fps">프레임레이트</param>
    /// <param name="crf">H.264 CRF 품질 (0=무손실, 23=기본, 51=최저)</param>
    public void StartExport(
        IntPtr timelineHandle,
        string outputPath,
        uint width,
        uint height,
        double fps,
        uint crf)
    {
        ThrowIfDisposed();

        if (_jobHandle != IntPtr.Zero)
            throw new InvalidOperationException("Export가 이미 진행 중입니다. Cancel() 후 다시 시도하세요.");

        if (timelineHandle == IntPtr.Zero)
            throw new ArgumentException("Timeline handle이 null입니다");

        if (string.IsNullOrEmpty(outputPath))
            throw new ArgumentException("출력 경로가 비어있습니다");

        // UTF-8 마샬링 (한글 경로 지원)
        IntPtr outputPathPtr = Marshal.StringToCoTaskMemUTF8(outputPath);
        try
        {
            int result = NativeMethods.exporter_start(
                timelineHandle,
                outputPathPtr,
                width,
                height,
                fps,
                crf,
                out _jobHandle);

            if (result != ErrorCodes.SUCCESS)
                throw new RustException($"exporter_start 실패: error {result}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(outputPathPtr);
        }
    }

    /// <summary>
    /// Export 진행률 (0~100)
    /// </summary>
    public int GetProgress()
    {
        if (_jobHandle == IntPtr.Zero) return 0;
        return (int)NativeMethods.exporter_get_progress(_jobHandle);
    }

    /// <summary>
    /// Export 완료 여부
    /// </summary>
    public bool IsFinished()
    {
        if (_jobHandle == IntPtr.Zero) return true;
        return NativeMethods.exporter_is_finished(_jobHandle) != 0;
    }

    /// <summary>
    /// Export 에러 메시지 (null이면 성공 또는 진행 중)
    /// </summary>
    public string? GetError()
    {
        if (_jobHandle == IntPtr.Zero) return null;

        int result = NativeMethods.exporter_get_error(_jobHandle, out IntPtr errorPtr);
        if (result != ErrorCodes.SUCCESS || errorPtr == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(errorPtr);
        }
        finally
        {
            NativeMethods.string_free(errorPtr);
        }
    }

    /// <summary>
    /// Export 취소
    /// </summary>
    public void Cancel()
    {
        if (_jobHandle != IntPtr.Zero)
        {
            NativeMethods.exporter_cancel(_jobHandle);
        }
    }

    /// <summary>
    /// Export 작업 정리 (완료/취소 후 호출)
    /// </summary>
    public void Cleanup()
    {
        if (_jobHandle != IntPtr.Zero)
        {
            NativeMethods.exporter_destroy(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExportService));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Cleanup();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VortexCut.Interop.Types;

namespace VortexCut.Interop.Services;

/// <summary>
/// Renderer SafeHandle - 자동 메모리 관리
/// </summary>
public class RendererHandle : SafeHandle
{
    public RendererHandle(IntPtr handle) : base(IntPtr.Zero, true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        return NativeMethods.renderer_destroy(handle) == ErrorCodes.SUCCESS;
    }
}

/// <summary>
/// 렌더링된 프레임 데이터
/// </summary>
public class RenderedFrame : IDisposable
{
    public uint Width { get; }
    public uint Height { get; }
    public byte[] Data { get; }
    public long TimestampMs { get; }

    private IntPtr _nativeDataPtr;
    private nuint _nativeDataSize;
    private bool _disposed;

    internal RenderedFrame(uint width, uint height, IntPtr dataPtr, nuint dataSize, long timestampMs)
    {
        Width = width;
        Height = height;
        TimestampMs = timestampMs;

        _nativeDataPtr = dataPtr;
        _nativeDataSize = dataSize;

        // 데이터 복사
        Data = new byte[(int)dataSize];
        Marshal.Copy(dataPtr, Data, 0, (int)dataSize);
    }

    public void Dispose()
    {
        if (!_disposed && _nativeDataPtr != IntPtr.Zero)
        {
            NativeMethods.renderer_free_frame_data(_nativeDataPtr, _nativeDataSize);
            _nativeDataPtr = IntPtr.Zero;
            _disposed = true;
        }
    }
}

/// <summary>
/// 렌더링 서비스
/// </summary>
public class RenderService : IDisposable
{
    private RendererHandle? _renderer;
    private bool _disposed;

    /// <summary>
    /// Renderer 생성
    /// </summary>
    public void CreateRenderer(TimelineHandle timeline)
    {
        ThrowIfDisposed();

        if (_renderer != null && !_renderer.IsInvalid)
        {
            throw new InvalidOperationException("Renderer already created");
        }

        if (timeline == null || timeline.IsInvalid)
        {
            throw new ArgumentException("Invalid timeline handle");
        }

        int result = NativeMethods.renderer_create(timeline.DangerousGetHandle(), out IntPtr rendererPtr);
        CheckError(result);

        _renderer = new RendererHandle(rendererPtr);
    }

    /// <summary>
    /// 프레임 렌더링
    /// </summary>
    public RenderedFrame RenderFrame(long timestampMs)
    {
        ThrowIfDisposed();
        ThrowIfNoRenderer();

        int result = NativeMethods.renderer_render_frame(
            _renderer!.DangerousGetHandle(),
            timestampMs,
            out uint width,
            out uint height,
            out IntPtr dataPtr,
            out nuint dataSize);

        CheckError(result);

        return new RenderedFrame(width, height, dataPtr, dataSize, timestampMs);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RenderService));
        }
    }

    private void ThrowIfNoRenderer()
    {
        if (_renderer == null || _renderer.IsInvalid)
        {
            throw new InvalidOperationException("Renderer not created");
        }
    }

    private static void CheckError(int errorCode)
    {
        if (errorCode != ErrorCodes.SUCCESS)
        {
            throw new RustException($"Rust error code: {errorCode}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _renderer?.Dispose();
            _disposed = true;
        }
    }
}

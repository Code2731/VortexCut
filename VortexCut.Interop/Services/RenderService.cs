using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VortexCut.Interop.Types;

namespace VortexCut.Interop.Services;

/// <summary>
/// 비디오 파일 메타데이터
/// </summary>
public record VideoInfo(long DurationMs, uint Width, uint Height, double Fps);

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
    /// <summary>
    /// 최대 허용 프레임 크기 (16K 해상도: 15360 x 8640 x 4 bytes = ~530MB)
    /// </summary>
    private const long MAX_FRAME_SIZE = 530 * 1024 * 1024; // 530MB

    public uint Width { get; }
    public uint Height { get; }
    public byte[] Data { get; }
    public long TimestampMs { get; }

    private IntPtr _nativeDataPtr;
    private nuint _nativeDataSize;
    private bool _disposed;

    internal RenderedFrame(uint width, uint height, IntPtr dataPtr, nuint dataSize, long timestampMs)
    {
        // 프레임 크기 검증 (메모리 폭탄 방지)
        if (width == 0 || height == 0)
        {
            throw new ArgumentException($"Invalid frame dimensions: {width}x{height}");
        }

        long expectedSize = (long)width * height * 4; // RGBA = 4 bytes per pixel
        if (expectedSize > MAX_FRAME_SIZE)
        {
            throw new OutOfMemoryException(
                $"Frame size too large: {width}x{height} = {expectedSize / (1024 * 1024)}MB (max: {MAX_FRAME_SIZE / (1024 * 1024)}MB)");
        }

        if ((long)dataSize > MAX_FRAME_SIZE || (long)dataSize < expectedSize / 2)
        {
            throw new ArgumentException(
                $"Invalid data size: {dataSize} bytes (expected ~{expectedSize} bytes for {width}x{height})");
        }

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
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer - Dispose를 호출하지 않았을 때 메모리 누수 방지
    /// </summary>
    ~RenderedFrame()
    {
        Dispose();
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
    /// Renderer 생성 (기존 렌더러는 수동으로 먼저 해제해야 함)
    /// </summary>
    public void CreateRenderer(TimelineHandle timeline)
    {
        ThrowIfDisposed();

        System.Diagnostics.Debug.WriteLine($"🎨 RenderService.CreateRenderer START");

        // 주의: 기존 렌더러가 있으면 먼저 DestroyRenderer() 호출 필요
        if (_renderer != null && !_renderer.IsInvalid)
        {
            throw new InvalidOperationException("Renderer already exists. Call DestroyRenderer() first.");
        }

        if (timeline == null || timeline.IsInvalid)
        {
            System.Diagnostics.Debug.WriteLine($"   ❌ Timeline handle is null or invalid!");
            throw new ArgumentException("Invalid timeline handle");
        }

        IntPtr timelinePtr = timeline.DangerousGetHandle();
        System.Diagnostics.Debug.WriteLine($"   Timeline handle: 0x{timelinePtr:X}");

        int result = NativeMethods.renderer_create(timelinePtr, out IntPtr rendererPtr);
        System.Diagnostics.Debug.WriteLine($"   renderer_create returned: {result}, rendererPtr=0x{rendererPtr:X}");
        CheckError(result);

        _renderer = new RendererHandle(rendererPtr);
        System.Diagnostics.Debug.WriteLine($"   ✅ Renderer created successfully!");
    }

    /// <summary>
    /// Renderer 명시적 해제
    /// </summary>
    public void DestroyRenderer()
    {
        if (_renderer != null && !_renderer.IsInvalid)
        {
            _renderer.Dispose();
            _renderer = null;
        }
    }

    /// <summary>
    /// 프레임 렌더링 (Mutex busy 시 null 반환 = 프레임 스킵)
    /// </summary>
    public RenderedFrame? RenderFrame(long timestampMs)
    {
        ThrowIfDisposed();
        ThrowIfNoRenderer();

        IntPtr rendererPtr = _renderer!.DangerousGetHandle();

        if (rendererPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Renderer pointer is null");
        }

        int result = NativeMethods.renderer_render_frame(
            rendererPtr,
            timestampMs,
            out uint width,
            out uint height,
            out IntPtr dataPtr,
            out nuint dataSize);

        CheckError(result);

        // Mutex busy로 프레임 스킵된 경우 (try_lock 실패 → width=0, height=0)
        if (width == 0 || height == 0)
        {
            return null;
        }

        return new RenderedFrame(width, height, dataPtr, dataSize, timestampMs);
    }

    /// <summary>
    /// 비디오 파일 정보 조회 (스태틱 메서드 - Renderer 인스턴스 불필요)
    /// </summary>
    public static VideoInfo GetVideoInfo(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Video file not found: {filePath}");
        }

        // UTF-8 수동 마샬링 (한글 경로 지원)
        IntPtr filePathPtr = Marshal.StringToCoTaskMemUTF8(filePath);
        try
        {
            int result = NativeMethods.get_video_info(
                filePathPtr,
                out long durationMs,
                out uint width,
                out uint height,
                out double fps);

            CheckError(result);

            return new VideoInfo(durationMs, width, height, fps);
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    /// <summary>
    /// 비디오 썸네일 생성 (스태틱 메서드 - Renderer 인스턴스 불필요)
    /// </summary>
    /// <param name="filePath">비디오 파일 경로</param>
    /// <param name="timestampMs">썸네일을 추출할 시간 (ms), 기본값 0ms (첫 프레임)</param>
    /// <param name="thumbWidth">썸네일 너비, 기본값 160px</param>
    /// <param name="thumbHeight">썸네일 높이, 기본값 90px</param>
    /// <returns>썸네일 프레임 데이터</returns>
    public static RenderedFrame GenerateThumbnail(
        string filePath,
        long timestampMs = 0,
        uint thumbWidth = 160,
        uint thumbHeight = 90)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Video file not found: {filePath}");
        }

        System.Diagnostics.Debug.WriteLine($"📸 RenderService.GenerateThumbnail: file={filePath}, timestamp={timestampMs}ms, size={thumbWidth}x{thumbHeight}");

        // UTF-8 수동 마샬링 (한글 경로 지원)
        IntPtr filePathPtr = Marshal.StringToCoTaskMemUTF8(filePath);
        int result;
        uint width, height;
        IntPtr dataPtr;
        nuint dataSize;
        try
        {
            result = NativeMethods.generate_video_thumbnail(
                filePathPtr,
                timestampMs,
                thumbWidth,
                thumbHeight,
                out width,
                out height,
                out dataPtr,
                out dataSize);
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePathPtr);
        }

        System.Diagnostics.Debug.WriteLine($"   generate_video_thumbnail returned: {result}, width={width}, height={height}, dataSize={dataSize}");
        CheckError(result);

        return new RenderedFrame(width, height, dataPtr, dataSize, timestampMs);
    }

    /// <summary>
    /// 프레임 캐시 클리어 (클립 편집/트림 변경 시 호출)
    /// </summary>
    public void ClearCache()
    {
        ThrowIfDisposed();
        ThrowIfNoRenderer();

        IntPtr rendererPtr = _renderer!.DangerousGetHandle();
        NativeMethods.renderer_clear_cache(rendererPtr);
    }

    /// <summary>
    /// 캐시 통계 조회 (디버깅용)
    /// </summary>
    public (uint CachedFrames, nuint CacheBytes) GetCacheStats()
    {
        ThrowIfDisposed();
        ThrowIfNoRenderer();

        IntPtr rendererPtr = _renderer!.DangerousGetHandle();
        NativeMethods.renderer_get_cache_stats(rendererPtr, out uint frames, out nuint bytes);
        return (frames, bytes);
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

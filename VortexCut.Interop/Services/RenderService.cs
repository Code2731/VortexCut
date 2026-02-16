using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VortexCut.Core.Interfaces;
using VortexCut.Core.Models;
using VortexCut.Interop.Types;

namespace VortexCut.Interop.Services;

/// <summary>
/// 썸네일 세션 SafeHandle - 디코더를 한 번 열고 여러 프레임 생성
/// </summary>
public class ThumbnailSessionHandle : SafeHandle
{
    public ThumbnailSessionHandle(IntPtr handle) : base(IntPtr.Zero, true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        return NativeMethods.thumbnail_session_destroy(handle) == ErrorCodes.SUCCESS;
    }
}

/// <summary>
/// 썸네일 세션 - 파일을 한 번 열고 여러 timestamp의 썸네일을 효율적으로 생성
/// 기존 GenerateThumbnail (매번 파일 Open/Close) 대비:
///   - 파일 Open 1회 (N회 → 1회)
///   - 스케일러가 직접 썸네일 해상도로 출력 (960x540 거치지 않음)
///   - 시간순 호출 시 forward decode 활용
/// </summary>
public class ThumbnailSession : IDisposable
{
    private ThumbnailSessionHandle? _handle;
    private bool _disposed;

    /// <summary>비디오 총 길이 (ms)</summary>
    public long DurationMs { get; }

    /// <summary>비디오 FPS</summary>
    public double Fps { get; }

    private ThumbnailSession(ThumbnailSessionHandle handle, long durationMs, double fps)
    {
        _handle = handle;
        DurationMs = durationMs;
        Fps = fps;
    }

    /// <summary>
    /// 썸네일 세션 생성
    /// </summary>
    public static ThumbnailSession Create(string filePath, uint thumbWidth, uint thumbHeight)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Video file not found: {filePath}");

        IntPtr filePathPtr = Marshal.StringToCoTaskMemUTF8(filePath);
        try
        {
            int result = NativeMethods.thumbnail_session_create(
                filePathPtr,
                thumbWidth,
                thumbHeight,
                out IntPtr sessionPtr,
                out long durationMs,
                out double fps);

            if (result != ErrorCodes.SUCCESS)
                throw new RustException($"thumbnail_session_create failed: error {result}");

            var handle = new ThumbnailSessionHandle(sessionPtr);
            return new ThumbnailSession(handle, durationMs, fps);
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    /// <summary>
    /// 특정 시간의 썸네일 생성 (세션 내에서 디코더 재사용)
    /// </summary>
    /// <returns>RenderedFrame 또는 null (프레임 스킵/EOF)</returns>
    public RenderedFrame? Generate(long timestampMs)
    {
        if (_disposed || _handle == null || _handle.IsInvalid)
            throw new ObjectDisposedException(nameof(ThumbnailSession));

        int result = NativeMethods.thumbnail_session_generate(
            _handle.DangerousGetHandle(),
            timestampMs,
            out uint width,
            out uint height,
            out IntPtr dataPtr,
            out nuint dataSize);

        if (result != ErrorCodes.SUCCESS)
            return null; // 디코딩 실패 → 스킵

        // width=0 = 프레임 스킵 (FrameSkipped/EOF)
        if (width == 0 || height == 0)
            return null;

        return new RenderedFrame(width, height, dataPtr, dataSize, timestampMs);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _handle?.Dispose();
            _handle = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

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
public class RenderedFrame : IRenderedFrame
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
/// PlaybackEngine SafeHandle
/// </summary>
public class PlaybackEngineHandle : SafeHandle
{
    public PlaybackEngineHandle(IntPtr handle) : base(IntPtr.Zero, true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        return NativeMethods.playback_engine_destroy(handle) == ErrorCodes.SUCCESS;
    }
}

/// <summary>
/// 렌더링 서비스
/// </summary>
public class RenderService : IRenderService
{
    private RendererHandle? _renderer;
    private PlaybackEngineHandle? _playbackEngine;
    private bool _disposed;

    /// <summary>
    /// Renderer 생성 (IntPtr 오버로드 — IRenderService 인터페이스 구현)
    /// </summary>
    public void CreateRenderer(IntPtr timelineHandle)
    {
        ThrowIfDisposed();

        if (_renderer != null && !_renderer.IsInvalid)
        {
            throw new InvalidOperationException("Renderer already exists. Call DestroyRenderer() first.");
        }

        if (timelineHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid timeline handle");
        }

        int result = NativeMethods.renderer_create(timelineHandle, out IntPtr rendererPtr);
        CheckError(result);

        _renderer = new RendererHandle(rendererPtr);

        // PlaybackEngine도 함께 생성 (임시로 주석 처리하여 앱 시작 확인)
        try
        {
            result = NativeMethods.playback_engine_create(timelineHandle, out IntPtr enginePtr);
            if (result == ErrorCodes.SUCCESS && enginePtr != IntPtr.Zero)
            {
                _playbackEngine = new PlaybackEngineHandle(enginePtr);
                System.Diagnostics.Debug.WriteLine("   ✅ PlaybackEngine created successfully!");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ⚠️ PlaybackEngine creation failed: {result}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"   ⚠️ PlaybackEngine not available: {ex.Message}");
            // PlaybackEngine 없이도 앱은 동작 (재생은 기존 방식 사용)
        }
    }

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

        // PlaybackEngine도 함께 생성 (임시로 예외 처리)
        try
        {
            result = NativeMethods.playback_engine_create(timelinePtr, out IntPtr enginePtr);
            System.Diagnostics.Debug.WriteLine($"   playback_engine_create returned: {result}, enginePtr=0x{enginePtr:X}");
            if (result == ErrorCodes.SUCCESS && enginePtr != IntPtr.Zero)
            {
                _playbackEngine = new PlaybackEngineHandle(enginePtr);
                System.Diagnostics.Debug.WriteLine($"   ✅ Renderer + PlaybackEngine created successfully!");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ⚠️ PlaybackEngine creation failed, using Renderer only");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"   ⚠️ PlaybackEngine not available: {ex.Message}");
        }
    }

    /// <summary>
    /// Renderer 명시적 해제
    /// </summary>
    public void DestroyRenderer()
    {
        // PlaybackEngine 먼저 정지 및 해제
        if (_playbackEngine != null && !_playbackEngine.IsInvalid)
        {
            _playbackEngine.Dispose();
            _playbackEngine = null;
        }

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
    /// 재생 모드 전환 (재생 시작/정지 시 호출)
    /// 재생 모드: forward_threshold=5000ms → seek 대신 forward decode (빠른 순차 재생)
    /// 스크럽 모드: forward_threshold=100ms → 즉시 seek (정확한 위치 이동)
    /// </summary>
    public void SetPlaybackMode(bool playback)
    {
        ThrowIfDisposed();
        ThrowIfNoRenderer();

        IntPtr rendererPtr = _renderer!.DangerousGetHandle();
        NativeMethods.renderer_set_playback_mode(rendererPtr, playback ? 1 : 0);
    }

    /// <summary>
    /// 클립 이펙트 설정 (Brightness, Contrast, Saturation, Temperature)
    /// 값 범위: -1.0 ~ 1.0, 0=원본
    /// </summary>
    public void SetClipEffects(ulong clipId, float brightness, float contrast, float saturation, float temperature)
    {
        ThrowIfDisposed();
        ThrowIfNoRenderer();

        IntPtr rendererPtr = _renderer!.DangerousGetHandle();
        NativeMethods.renderer_set_clip_effects(rendererPtr, clipId, brightness, contrast, saturation, temperature);
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

    // === IRenderService instance wrappers (static 메서드 위임) ===

    /// <summary>
    /// 비디오 파일 정보 조회 (instance wrapper → static)
    /// </summary>
    VideoInfo IRenderService.GetVideoInfo(string filePath) => GetVideoInfo(filePath);

    /// <summary>
    /// 비디오 썸네일 생성 (instance wrapper → static)
    /// </summary>
    IRenderedFrame IRenderService.GenerateThumbnail(string filePath, long timestampMs, uint thumbWidth, uint thumbHeight)
        => GenerateThumbnail(filePath, timestampMs, thumbWidth, thumbHeight);

    // === IRenderService RenderFrame wrapper ===

    /// <summary>
    /// 프레임 렌더링 (IRenderedFrame 반환 — IRenderService 인터페이스 구현)
    /// </summary>
    IRenderedFrame? IRenderService.RenderFrame(long timestampMs) => RenderFrame(timestampMs);

    // === PlaybackEngine (재생 전용 백그라운드 프리페치) ===

    /// <summary>
    /// PlaybackEngine 시작 (백그라운드 프리페치 시작)
    /// </summary>
    public void StartPlaybackEngine(long startMs)
    {
        ThrowIfDisposed();

        if (_playbackEngine == null || _playbackEngine.IsInvalid)
        {
            throw new InvalidOperationException("PlaybackEngine not initialized. Call CreateRenderer() first.");
        }

        IntPtr enginePtr = _playbackEngine.DangerousGetHandle();
        int result = NativeMethods.playback_engine_start(enginePtr, startMs);
        CheckError(result);

        System.Diagnostics.Debug.WriteLine($"[PlaybackEngine] Started from {startMs}ms");
    }

    /// <summary>
    /// PlaybackEngine 정지 (백그라운드 스레드 종료)
    /// </summary>
    public void StopPlaybackEngine()
    {
        ThrowIfDisposed();

        if (_playbackEngine == null || _playbackEngine.IsInvalid)
        {
            return; // 이미 정지됨
        }

        IntPtr enginePtr = _playbackEngine.DangerousGetHandle();
        int result = NativeMethods.playback_engine_stop(enginePtr);
        CheckError(result);

        System.Diagnostics.Debug.WriteLine($"[PlaybackEngine] Stopped");
    }

    /// <summary>
    /// PlaybackEngine에서 프레임 조회 (디코딩 없음, 즉시 반환)
    /// </summary>
    // 프레임 데이터 진단 카운터 (첫 5프레임만 상세 로그)
    private int _tryGetDiagCount = 0;

    public RenderedFrame? TryGetPlaybackFrame(long timestampMs)
    {
        ThrowIfDisposed();

        if (_playbackEngine == null || _playbackEngine.IsInvalid)
        {
            return null;
        }

        IntPtr enginePtr = _playbackEngine.DangerousGetHandle();

        int result = NativeMethods.playback_engine_try_get_frame(
            enginePtr,
            timestampMs,
            out uint width,
            out uint height,
            out IntPtr dataPtr,
            out nuint dataSize,
            out long actualTimestampMs);

        CheckError(result);

        // width=0 → 프레임 없음
        if (width == 0 || height == 0)
        {
            var reason = (ulong)dataSize switch
            {
                1 => "ENGINE_MUTEX_BUSY",
                2 => "QUEUE_EMPTY",
                _ => $"UNKNOWN({(ulong)dataSize})"
            };
            System.Diagnostics.Debug.WriteLine($"[PlaybackEngine] MISS at {timestampMs}ms: {reason}");
            return null;
        }

        // 진단: 첫 5프레임에 실제 timestamp + 첫 4바이트(RGBA) 로그
        if (_tryGetDiagCount < 5)
        {
            _tryGetDiagCount++;
            string pixelHex = "N/A";
            if (dataPtr != IntPtr.Zero && (int)dataSize >= 4)
            {
                byte[] px = new byte[4];
                Marshal.Copy(dataPtr, px, 0, 4);
                pixelHex = $"{px[0]:X2}{px[1]:X2}{px[2]:X2}{px[3]:X2}";
            }
            System.Diagnostics.Debug.WriteLine(
                $"[PlaybackEngine DIAG #{_tryGetDiagCount}] req={timestampMs}ms, actual={actualTimestampMs}ms, diff={timestampMs - actualTimestampMs}ms, px0={pixelHex}, {width}x{height}");
        }

        return new RenderedFrame(width, height, dataPtr, dataSize, actualTimestampMs);
    }

    /// <summary>
    /// PlaybackEngine 진단 카운터 리셋 (재생 시작 시 호출)
    /// </summary>
    public void ResetPlaybackDiag()
    {
        _tryGetDiagCount = 0;
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

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VortexCut.Interop.Types;

namespace VortexCut.Interop.Services;

/// <summary>
/// Timeline SafeHandle - 자동 메모리 관리
/// </summary>
public class TimelineHandle : SafeHandle
{
    public TimelineHandle(IntPtr handle) : base(IntPtr.Zero, true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        return NativeMethods.timeline_destroy(handle) == ErrorCodes.SUCCESS;
    }
}

/// <summary>
/// Timeline 관리 서비스
/// </summary>
public class TimelineService : IDisposable
{
    private TimelineHandle? _timeline;
    private bool _disposed;

    /// <summary>
    /// 타임라인 생성 (기존 타임라인은 수동으로 먼저 해제해야 함)
    /// </summary>
    public void CreateTimeline(uint width, uint height, double fps)
    {
        ThrowIfDisposed();

        // 주의: 기존 타임라인이 있으면 먼저 DestroyTimeline() 호출 필요
        if (_timeline != null && !_timeline.IsInvalid)
        {
            throw new InvalidOperationException("Timeline already exists. Call DestroyTimeline() first.");
        }

        int result = NativeMethods.timeline_create(width, height, fps, out IntPtr timelinePtr);
        CheckError(result);

        _timeline = new TimelineHandle(timelinePtr);
    }

    /// <summary>
    /// 타임라인 명시적 해제
    /// </summary>
    public void DestroyTimeline()
    {
        if (_timeline != null && !_timeline.IsInvalid)
        {
            _timeline.Dispose();
            _timeline = null;
        }
    }

    /// <summary>
    /// 타임라인 핸들 가져오기 (RenderService 생성용)
    /// </summary>
    public TimelineHandle GetTimelineHandle()
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        return _timeline!;
    }

    /// <summary>
    /// 비디오 트랙 추가
    /// </summary>
    public ulong AddVideoTrack()
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_add_video_track(_timeline!.DangerousGetHandle(), out ulong trackId);
        CheckError(result);

        return trackId;
    }

    /// <summary>
    /// 오디오 트랙 추가
    /// </summary>
    public ulong AddAudioTrack()
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_add_audio_track(_timeline!.DangerousGetHandle(), out ulong trackId);
        CheckError(result);

        return trackId;
    }

    /// <summary>
    /// 비디오 클립 추가
    /// </summary>
    public ulong AddVideoClip(ulong trackId, string filePath, long startTimeMs, long durationMs)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        IntPtr filePathPtr = Marshal.StringToCoTaskMemUTF8(filePath);
        try
        {
            int result = NativeMethods.timeline_add_video_clip(
                _timeline!.DangerousGetHandle(),
                trackId,
                filePathPtr,
                startTimeMs,
                durationMs,
                out ulong clipId);

            CheckError(result);
            return clipId;
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    /// <summary>
    /// 오디오 클립 추가
    /// </summary>
    public ulong AddAudioClip(ulong trackId, string filePath, long startTimeMs, long durationMs)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        IntPtr filePathPtr = Marshal.StringToCoTaskMemUTF8(filePath);
        try
        {
            int result = NativeMethods.timeline_add_audio_clip(
                _timeline!.DangerousGetHandle(),
                trackId,
                filePathPtr,
                startTimeMs,
                durationMs,
                out ulong clipId);

            CheckError(result);
            return clipId;
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    /// <summary>
    /// 비디오 클립 제거
    /// </summary>
    public void RemoveVideoClip(ulong trackId, ulong clipId)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_remove_video_clip(_timeline!.DangerousGetHandle(), trackId, clipId);
        CheckError(result);
    }

    /// <summary>
    /// 오디오 클립 제거
    /// </summary>
    public void RemoveAudioClip(ulong trackId, ulong clipId)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_remove_audio_clip(_timeline!.DangerousGetHandle(), trackId, clipId);
        CheckError(result);
    }

    /// <summary>
    /// 비디오 클립 트림 설정 (Razor 분할 시 소스 오프셋 지정)
    /// </summary>
    public void SetVideoClipTrim(ulong trackId, ulong clipId, long trimStartMs, long trimEndMs)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_set_video_clip_trim(
            _timeline!.DangerousGetHandle(), trackId, clipId, trimStartMs, trimEndMs);
        CheckError(result);
    }

    /// <summary>
    /// 타임라인 총 길이 가져오기 (ms)
    /// </summary>
    public long GetDuration()
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_get_duration(_timeline!.DangerousGetHandle(), out long durationMs);
        CheckError(result);

        return durationMs;
    }

    /// <summary>
    /// 비디오 트랙 개수
    /// </summary>
    public int GetVideoTrackCount()
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_get_video_track_count(_timeline!.DangerousGetHandle(), out nuint count);
        CheckError(result);

        return (int)count;
    }

    /// <summary>
    /// 오디오 트랙 개수
    /// </summary>
    public int GetAudioTrackCount()
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_get_audio_track_count(_timeline!.DangerousGetHandle(), out nuint count);
        CheckError(result);

        return (int)count;
    }

    /// <summary>
    /// 특정 비디오 트랙의 클립 개수
    /// </summary>
    public int GetVideoClipCount(ulong trackId)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_get_video_clip_count(_timeline!.DangerousGetHandle(), trackId, out nuint count);
        CheckError(result);

        return (int)count;
    }

    /// <summary>
    /// 클립 볼륨 설정 (0.0~2.0)
    /// </summary>
    public void SetClipVolume(ulong clipId, float volume)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_set_clip_volume(
            _timeline!.DangerousGetHandle(), clipId, volume);
        CheckError(result);
    }

    /// <summary>
    /// 클립 속도 설정 (0.25~4.0)
    /// </summary>
    public void SetClipSpeed(ulong clipId, double speed)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_set_clip_speed(
            _timeline!.DangerousGetHandle(), clipId, speed);
        CheckError(result);
    }

    /// <summary>
    /// 오디오 클립 페이드 설정
    /// </summary>
    public void SetClipFade(ulong clipId, long fadeInMs, long fadeOutMs)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_set_clip_fade(
            _timeline!.DangerousGetHandle(), clipId, fadeInMs, fadeOutMs);
        CheckError(result);
    }

    /// <summary>
    /// 클립 트랜지션 타입 설정 (비디오 트랙 only)
    /// </summary>
    public void SetClipTransition(ulong clipId, uint transitionType)
    {
        ThrowIfDisposed();
        ThrowIfNoTimeline();

        int result = NativeMethods.timeline_set_clip_transition(
            _timeline!.DangerousGetHandle(), clipId, transitionType);
        CheckError(result);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TimelineService));
        }
    }

    private void ThrowIfNoTimeline()
    {
        if (_timeline == null || _timeline.IsInvalid)
        {
            throw new InvalidOperationException("Timeline not created");
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
            _timeline?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Rust FFI 에러 예외
/// </summary>
public class RustException : Exception
{
    public RustException(string message) : base(message) { }
}

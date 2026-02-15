using VortexCut.Core.Interfaces;

namespace VortexCut.Interop.Services;

/// <summary>
/// 실시간 오디오 재생 서비스 (Rust cpal 엔진 P/Invoke 래퍼)
/// </summary>
public class AudioPlaybackService : IAudioPlaybackService
{
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>
    /// 오디오 재생 중인지 여부
    /// </summary>
    public bool IsActive => _handle != IntPtr.Zero;

    /// <summary>
    /// 오디오 재생 시작
    /// </summary>
    /// <param name="timelineHandle">Rust Timeline의 Arc 포인터</param>
    /// <param name="startTimeMs">재생 시작 위치 (타임라인 시간)</param>
    public void Start(IntPtr timelineHandle, long startTimeMs)
    {
        // 이전 재생 정리
        Stop();

        if (timelineHandle == IntPtr.Zero)
            return;

        var result = NativeMethods.audio_playback_start(timelineHandle, startTimeMs, out var handle);
        if (result == 0)
        {
            _handle = handle;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[AudioPlayback] Start failed: error={result}");
        }
    }

    /// <summary>
    /// 오디오 재생 정지
    /// </summary>
    public void Stop()
    {
        if (_handle == IntPtr.Zero) return;

        NativeMethods.audio_playback_stop(_handle);
        NativeMethods.audio_playback_destroy(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>
    /// 오디오 일시정지
    /// </summary>
    public void Pause()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.audio_playback_pause(_handle);
    }

    /// <summary>
    /// 오디오 재개
    /// </summary>
    public void Resume()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.audio_playback_resume(_handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}

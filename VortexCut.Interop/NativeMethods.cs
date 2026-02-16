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
    /// filePath: 원본 경로, proxyPath: 프리뷰용 Proxy (IntPtr.Zero면 원본만 사용)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_add_video_clip(
        IntPtr timeline,
        ulong trackId,
        IntPtr filePath,
        IntPtr proxyPath,
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
    /// 비디오 클립 트림 설정 (Razor 분할용)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_set_video_clip_trim(
        IntPtr timeline, ulong trackId, ulong clipId,
        long trimStartMs, long trimEndMs);

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

    /// <summary>
    /// 특정 비디오 트랙의 클립 개수 가져오기
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_get_video_clip_count(IntPtr timeline, ulong trackId, out nuint outCount);

    /// <summary>
    /// 클립 볼륨 설정 (비디오/오디오 트랙 모두 검색)
    /// volume: 0.0~2.0
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_set_clip_volume(IntPtr timeline, ulong clipId, float volume);

    /// <summary>
    /// 클립 속도 설정 (비디오/오디오 트랙 모두 검색)
    /// speed: 0.25~4.0
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_set_clip_speed(IntPtr timeline, ulong clipId, double speed);

    /// <summary>
    /// 오디오 클립 페이드 설정
    /// fadeInMs, fadeOutMs: 0 = 페이드 없음
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_set_clip_fade(IntPtr timeline, ulong clipId, long fadeInMs, long fadeOutMs);

    /// <summary>
    /// 클립 트랜지션 타입 설정 (비디오 트랙 only)
    /// transitionType: 0=None, 1=Crossfade, 2=FadeBlack, 3=WipeLeft, 4=WipeRight, 5=WipeUp, 6=WipeDown
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_set_clip_transition(IntPtr timeline, ulong clipId, uint transitionType);

    /// <summary>
    /// 트랙 뮤트 설정 (비디오 + 오디오 트랙 공용)
    /// muted: 0=unmute, 1=mute
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int timeline_set_track_muted(IntPtr timeline, ulong trackId, int muted);

    // ==================== Video Info Functions ====================

    /// <summary>
    /// 비디오 파일 정보 조회 (duration, width, height, fps)
    /// filePath는 UTF-8 인코딩된 IntPtr (Marshal.StringToCoTaskMemUTF8 사용)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int get_video_info(
        IntPtr filePath,
        out long outDurationMs,
        out uint outWidth,
        out uint outHeight,
        out double outFps);

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
    /// 재생 모드 설정 (재생 시작 시 1, 정지 시 0)
    /// 재생 모드: forward_threshold=5000ms (seek 대신 forward decode)
    /// 스크럽 모드: forward_threshold=100ms (즉시 seek)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_set_playback_mode(IntPtr renderer, int playback);

    /// <summary>
    /// 프레임 캐시 클리어 (클립 편집 시 호출)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_clear_cache(IntPtr renderer);

    /// <summary>
    /// 캐시 통계 조회 (디버깅/모니터링)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_get_cache_stats(
        IntPtr renderer,
        out uint outCachedFrames,
        out nuint outCacheBytes);

    /// <summary>
    /// 클립 이펙트 설정 (Inspector Color 탭 Slider에서 호출)
    /// brightness, contrast, saturation, temperature: -1.0 ~ 1.0 (0=원본)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_set_clip_effects(
        IntPtr renderer,
        ulong clipId,
        float brightness,
        float contrast,
        float saturation,
        float temperature);

    /// <summary>
    /// 렌더링된 프레임 데이터 해제
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int renderer_free_frame_data(IntPtr data, nuint size);

    /// <summary>
    /// 비디오 썸네일 생성 (스탠드얼론 함수)
    /// filePath는 UTF-8 인코딩된 IntPtr (Marshal.StringToCoTaskMemUTF8 사용)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int generate_video_thumbnail(
        IntPtr filePath,
        long timestampMs,
        uint thumbWidth,
        uint thumbHeight,
        out uint outWidth,
        out uint outHeight,
        out IntPtr outData,
        out nuint outDataSize);

    // ==================== Thumbnail Session Functions ====================

    /// <summary>
    /// 썸네일 세션 생성 (디코더를 한 번 열고 여러 프레임 생성)
    /// filePath는 UTF-8 인코딩된 IntPtr
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int thumbnail_session_create(
        IntPtr filePath,
        uint thumbWidth,
        uint thumbHeight,
        out IntPtr outSession,
        out long outDurationMs,
        out double outFps);

    /// <summary>
    /// 세션에서 특정 시간의 썸네일 생성
    /// width=0, height=0이면 프레임 스킵 (FrameSkipped/EOF)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int thumbnail_session_generate(
        IntPtr session,
        long timestampMs,
        out uint outWidth,
        out uint outHeight,
        out IntPtr outData,
        out nuint outDataSize);

    /// <summary>
    /// 썸네일 세션 파괴
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int thumbnail_session_destroy(IntPtr session);

    // ==================== Audio Waveform Functions ====================

    /// <summary>
    /// 오디오 파형 피크 데이터 추출
    /// filePath는 UTF-8 인코딩된 IntPtr (Marshal.StringToCoTaskMemUTF8 사용)
    /// samples_per_peak: 다운샘플 비율 (예: 1024 → 1024 샘플당 1 피크)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int extract_audio_peaks(
        IntPtr filePath,
        uint samplesPerPeak,
        out IntPtr outPeaks,
        out uint outPeakCount,
        out uint outChannels,
        out uint outSampleRate,
        out long outDurationMs);

    /// <summary>
    /// 오디오 피크 데이터 메모리 해제
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int free_audio_peaks(IntPtr peaks, uint count);

    // ==================== Audio Playback Functions ====================

    /// <summary>
    /// 오디오 재생 시작
    /// timeline: Rust Timeline의 Arc 포인터 (소유권 변경 없음)
    /// startTimeMs: 재생 시작 위치
    /// outHandle: AudioPlayback 핸들 반환
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_playback_start(
        IntPtr timeline,
        long startTimeMs,
        out IntPtr outHandle);

    /// <summary>
    /// 오디오 재생 정지
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_playback_stop(IntPtr handle);

    /// <summary>
    /// 오디오 일시정지
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_playback_pause(IntPtr handle);

    /// <summary>
    /// 오디오 재개
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_playback_resume(IntPtr handle);

    /// <summary>
    /// 오디오 재생 객체 파괴 (메모리 해제)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_playback_destroy(IntPtr handle);

    /// <summary>
    /// 오디오 현재 재생 위치 조회 (ms) — cpal 소비 샘플 기준
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_playback_get_position(IntPtr handle, out long outPositionMs);

    // ==================== Export Functions ====================

    /// <summary>
    /// Export 시작 (백그라운드 스레드에서 실행)
    /// timeline: Rust Timeline의 Arc 포인터
    /// outputPath: UTF-8 인코딩된 출력 경로
    /// outJob: ExportJob 핸들 반환
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_start(
        IntPtr timeline,
        IntPtr outputPath,
        uint width,
        uint height,
        double fps,
        uint crf,
        out IntPtr outJob);

    /// <summary>
    /// Export 진행률 (0~100)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint exporter_get_progress(IntPtr job);

    /// <summary>
    /// Export 완료 여부 (1=완료, 0=진행중)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_is_finished(IntPtr job);

    /// <summary>
    /// Export 에러 메시지 (없으면 null)
    /// 반환된 문자열은 string_free()로 해제 필요
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_get_error(IntPtr job, out IntPtr outError);

    /// <summary>
    /// Export 취소
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_cancel(IntPtr job);

    /// <summary>
    /// ExportJob 파괴 (메모리 해제)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_destroy(IntPtr job);

    // ==================== Export Subtitle Functions ====================

    /// <summary>
    /// 자막 오버레이 목록 생성
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr exporter_create_subtitle_list();

    /// <summary>
    /// 자막 오버레이 추가 (RGBA 비트맵)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_subtitle_list_add(
        IntPtr list,
        long startMs,
        long endMs,
        int x,
        int y,
        uint width,
        uint height,
        IntPtr rgbaPtr,
        uint rgbaLen);

    /// <summary>
    /// 자막 포함 Export 시작 (v2)
    /// subtitleList: null이면 자막 없음, 소유권 Rust로 이전
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_start_v2(
        IntPtr timeline,
        IntPtr outputPath,
        uint width,
        uint height,
        double fps,
        uint crf,
        IntPtr subtitleList,
        out IntPtr outJob);

    /// <summary>
    /// 자막 포함 Export 시작 (v3) — 인코더 타입 선택 지원
    /// encoderType: 0=Auto, 1=Software, 2=NVENC, 3=QSV, 4=AMF
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_start_v3(
        IntPtr timeline,
        IntPtr outputPath,
        uint width,
        uint height,
        double fps,
        uint crf,
        uint encoderType,
        IntPtr subtitleList,
        out IntPtr outJob);

    /// <summary>
    /// 사용 가능한 인코더 탐지 (비트마스크)
    /// bit 0=libx264, bit 1=NVENC, bit 2=QSV, bit 3=AMF
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint exporter_detect_encoders();

    /// <summary>
    /// 자막 오버레이 목록 해제 (Export 취소 시에만 사용)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int exporter_free_subtitle_list(IntPtr list);

    // ============================================================
    // PlaybackEngine FFI (재생 전용 백그라운드 프리페치)
    // ============================================================

    /// <summary>
    /// PlaybackEngine 생성
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int playback_engine_create(
        IntPtr timeline,
        out IntPtr outEngine);

    /// <summary>
    /// PlaybackEngine 시작 (백그라운드 프리페치 시작)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int playback_engine_start(
        IntPtr engine,
        long startMs);

    /// <summary>
    /// PlaybackEngine 정지 (백그라운드 스레드 종료)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int playback_engine_stop(IntPtr engine);

    /// <summary>
    /// timestamp에 가장 가까운 프레임 조회 (디코딩 없음, 즉시 반환)
    /// outActualTimestampMs: 큐에서 실제 매칭된 프레임 timestamp (진단용)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int playback_engine_try_get_frame(
        IntPtr engine,
        long timestampMs,
        out uint outWidth,
        out uint outHeight,
        out IntPtr outDataPtr,
        out nuint outDataSize,
        out long outActualTimestampMs);

    /// <summary>
    /// PlaybackEngine 파괴
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int playback_engine_destroy(IntPtr engine);
}

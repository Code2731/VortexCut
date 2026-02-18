using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VortexCut.Core.Services;

namespace VortexCut.UI.Services;

/// <summary>
/// Whisper 음성 인식 서비스
/// TranscribeAsync: 미디어 파일 → SubtitleEntry 리스트
/// Export 폴링 패턴과 동일 (100ms 간격)
/// </summary>
public class WhisperService
{
    /// <summary>
    /// 미디어 파일에서 음성 인식하여 자막 세그먼트 반환
    /// </summary>
    /// <param name="mediaPath">미디어 파일 경로 (비디오/오디오)</param>
    /// <param name="modelPath">ggml 모델 파일 경로</param>
    /// <param name="language">"ko"/"en"/"ja"/... 또는 "" (자동 감지)</param>
    /// <param name="progress">진행률 콜백 (0~100)</param>
    /// <param name="ct">취소 토큰</param>
    public async Task<List<SubtitleEntry>> TranscribeAsync(
        string mediaPath,
        string modelPath,
        string language,
        IProgress<int> progress,
        CancellationToken ct)
    {
        // UTF-8로 경로 마샬링
        var audioPathPtr = Marshal.StringToCoTaskMemUTF8(mediaPath);
        var modelPathPtr = Marshal.StringToCoTaskMemUTF8(modelPath);
        var languagePtr = Marshal.StringToCoTaskMemUTF8(language);

        IntPtr jobHandle = IntPtr.Zero;

        try
        {
            // Rust 백그라운드 스레드 시작
            int ret = WhisperInterop.transcriber_start(
                audioPathPtr, modelPathPtr, languagePtr, out jobHandle);

            if (ret != 0 || jobHandle == IntPtr.Zero)
                throw new InvalidOperationException($"transcriber_start 실패: {ret}");

            // 취소 시: C# 태스크만 종료, Rust whisper 스레드는 자연 완료 대기
            // (transcriber_request_abort 제거 — GGML abort() 호출로 앱 전체 종료 유발 가능)

            // 100ms 폴링
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);

                uint prog = WhisperInterop.transcriber_get_progress(jobHandle);
                progress.Report((int)prog);

                if (WhisperInterop.transcriber_is_finished(jobHandle) != 0)
                    break;
            }

            // 에러 확인 (WARN: 접두사 = 경고, 예외 아님)
            WhisperInterop.transcriber_get_error(jobHandle, out IntPtr errPtr);
            if (errPtr != IntPtr.Zero)
            {
                string errMsg = Marshal.PtrToStringUTF8(errPtr) ?? "알 수 없는 오류";
                WhisperInterop.transcriber_free_string(errPtr);
                if (!errMsg.StartsWith("WARN:"))
                    throw new InvalidOperationException($"Whisper 오류: {errMsg}");
                // WARN은 무시 (0개 경고) — segments가 빈 채로 반환됨
            }

            // 세그먼트 수집
            return CollectSegments(jobHandle);
        }
        finally
        {
            Marshal.FreeCoTaskMem(audioPathPtr);
            Marshal.FreeCoTaskMem(modelPathPtr);
            Marshal.FreeCoTaskMem(languagePtr);

            if (jobHandle != IntPtr.Zero)
                WhisperInterop.transcriber_destroy(jobHandle);
        }
    }

    /// <summary>
    /// TranscriberJob에서 세그먼트를 수집하여 SubtitleEntry 리스트 반환
    /// </summary>
    private static List<SubtitleEntry> CollectSegments(IntPtr jobHandle)
    {
        IntPtr segPtr = WhisperInterop.transcriber_get_segments(jobHandle, out uint count);
        var result = new List<SubtitleEntry>((int)count);

        if (segPtr == IntPtr.Zero || count == 0)
            return result;

        try
        {
            int structSize = Marshal.SizeOf<CTranscriptSegment>();
            for (uint i = 0; i < count; i++)
            {
                IntPtr elemPtr = segPtr + (int)(i * structSize);
                var seg = Marshal.PtrToStructure<CTranscriptSegment>(elemPtr);
                string text = seg.TextPtr != IntPtr.Zero
                    ? Marshal.PtrToStringUTF8(seg.TextPtr) ?? string.Empty
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(new SubtitleEntry(
                        (int)(i + 1),
                        seg.StartMs,
                        seg.EndMs,
                        text));
                }
            }
        }
        finally
        {
            WhisperInterop.transcriber_free_segments(segPtr, count);
        }

        return result;
    }
}

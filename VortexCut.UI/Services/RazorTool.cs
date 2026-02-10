using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Services;

/// <summary>
/// Razor 도구 (클립 자르기)
/// </summary>
public class RazorTool
{
    private readonly TimelineViewModel _timeline;
    private ulong _nextClipId = 1000; // TODO: ProjectService에서 ID 생성

    public RazorTool(TimelineViewModel timeline)
    {
        _timeline = timeline;
    }

    /// <summary>
    /// 특정 시간에 클립 자르기
    /// </summary>
    public void CutClipAtTime(ClipModel clip, long cutTimeMs)
    {
        // 자르기 위치가 클립 범위 밖이면 무시
        if (cutTimeMs <= clip.StartTimeMs || cutTimeMs >= clip.EndTimeMs)
            return;

        // 첫 번째 부분: 원본 클립 수정
        var originalDuration = clip.DurationMs;
        var originalEnd = clip.EndTimeMs;
        clip.DurationMs = cutTimeMs - clip.StartTimeMs;

        // 두 번째 부분: 새 클립 생성
        var newClip = new ClipModel
        {
            Id = _nextClipId++,
            FilePath = clip.FilePath,
            StartTimeMs = cutTimeMs,
            DurationMs = originalEnd - cutTimeMs,
            TrackIndex = clip.TrackIndex
            // TODO: TrimStartMs 추가 후 조정
            // TrimStartMs = clip.TrimStartMs + (cutTimeMs - clip.StartTimeMs)
        };

        _timeline.Clips.Add(newClip);

        // TODO: Rust ProjectService.SplitClip() 호출
        // _projectService.SplitClip(clip.Id, cutTimeMs);
    }

    /// <summary>
    /// 특정 시간에 모든 트랙의 클립 동시 자르기
    /// </summary>
    public void CutAllTracksAtTime(long cutTimeMs)
    {
        // 자를 클립 목록 (루프 중 컬렉션 변경 방지)
        var clipsTocut = _timeline.Clips
            .Where(c => c.StartTimeMs < cutTimeMs && c.EndTimeMs > cutTimeMs)
            .ToList();

        foreach (var clip in clipsTocut)
        {
            CutClipAtTime(clip, cutTimeMs);
        }
    }
}

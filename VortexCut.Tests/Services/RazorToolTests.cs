using VortexCut.Core.Models;
using VortexCut.Core.Interfaces;
using VortexCut.UI.Services;
using VortexCut.UI.ViewModels;
using VortexCut.Interop.Services;
using Moq;
using Xunit;

namespace VortexCut.Tests.Services;

/// <summary>
/// RazorTool 단위 테스트
/// </summary>
public class RazorToolTests
{
    private TimelineViewModel CreateTimelineViewModel()
    {
        var mockRenderService = new Mock<IRenderService>();
        var timelineService = new TimelineService();
        var projectService = new ProjectService(mockRenderService.Object, timelineService);
        return new TimelineViewModel(projectService);
    }

    [Fact]
    public void CutClipAtTime_ValidCut_SplitsClipInTwo()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        var clip = new ClipModel
        {
            Id = 1,
            FilePath = "test.mp4",
            StartTimeMs = 0,
            DurationMs = 10000,
            TrackIndex = 0
        };
        timeline.Clips.Add(clip);

        var razorTool = new RazorTool(timeline);

        // Act: 5000ms에서 자르기
        razorTool.CutClipAtTime(clip, 5000);

        // Assert: 2개 클립으로 분할됨
        Assert.Equal(2, timeline.Clips.Count);

        // 첫 번째 클립 (원본 수정)
        Assert.Equal(0, clip.StartTimeMs);
        Assert.Equal(5000, clip.DurationMs);
        Assert.Equal(5000, clip.EndTimeMs);

        // 두 번째 클립 (새로 생성)
        var newClip = timeline.Clips.FirstOrDefault(c => c.Id != 1);
        Assert.NotNull(newClip);
        Assert.Equal(5000, newClip.StartTimeMs);
        Assert.Equal(5000, newClip.DurationMs);
        Assert.Equal(10000, newClip.EndTimeMs);
        Assert.Equal("test.mp4", newClip.FilePath);
        Assert.Equal(0, newClip.TrackIndex);
    }

    [Fact]
    public void CutClipAtTime_CutAtStart_DoesNothing()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        var clip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 1000,
            DurationMs = 5000,
            TrackIndex = 0
        };
        timeline.Clips.Add(clip);

        var razorTool = new RazorTool(timeline);

        // Act: 시작점에서 자르기 시도
        razorTool.CutClipAtTime(clip, 1000);

        // Assert: 변경 없음
        Assert.Single(timeline.Clips);
        Assert.Equal(5000, clip.DurationMs);
    }

    [Fact]
    public void CutClipAtTime_CutAtEnd_DoesNothing()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        var clip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 1000,
            DurationMs = 5000, // EndTimeMs = 6000
            TrackIndex = 0
        };
        timeline.Clips.Add(clip);

        var razorTool = new RazorTool(timeline);

        // Act: 끝점에서 자르기 시도
        razorTool.CutClipAtTime(clip, 6000);

        // Assert: 변경 없음
        Assert.Single(timeline.Clips);
        Assert.Equal(5000, clip.DurationMs);
    }

    [Fact]
    public void CutClipAtTime_CutOutsideClip_DoesNothing()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        var clip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 1000,
            DurationMs = 5000,
            TrackIndex = 0
        };
        timeline.Clips.Add(clip);

        var razorTool = new RazorTool(timeline);

        // Act: 클립 범위 밖에서 자르기 시도
        razorTool.CutClipAtTime(clip, 7000);

        // Assert: 변경 없음
        Assert.Single(timeline.Clips);
        Assert.Equal(5000, clip.DurationMs);
    }

    [Fact]
    public void CutAllTracksAtTime_MultipleTracks_CutsAll()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var clip1 = new ClipModel
        {
            Id = 1,
            FilePath = "video1.mp4",
            StartTimeMs = 0,
            DurationMs = 10000,
            TrackIndex = 0
        };
        var clip2 = new ClipModel
        {
            Id = 2,
            FilePath = "video2.mp4",
            StartTimeMs = 2000,
            DurationMs = 8000, // End: 10000
            TrackIndex = 1
        };
        var clip3 = new ClipModel
        {
            Id = 3,
            FilePath = "audio.mp3",
            StartTimeMs = 1000,
            DurationMs = 6000, // End: 7000
            TrackIndex = 2
        };

        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);
        timeline.Clips.Add(clip3);

        var razorTool = new RazorTool(timeline);

        // Act: 5000ms에서 모든 트랙 자르기
        razorTool.CutAllTracksAtTime(5000);

        // Assert: 3개 → 6개 클립으로 증가
        Assert.Equal(6, timeline.Clips.Count);

        // clip1 분할 확인 (0-10000 → 0-5000 + 5000-10000)
        Assert.Equal(5000, clip1.DurationMs);
        var clip1_part2 = timeline.Clips.FirstOrDefault(c =>
            c.TrackIndex == 0 && c.StartTimeMs == 5000);
        Assert.NotNull(clip1_part2);
        Assert.Equal(5000, clip1_part2.DurationMs);

        // clip2 분할 확인 (2000-10000 → 2000-5000 + 5000-10000)
        Assert.Equal(3000, clip2.DurationMs); // 5000 - 2000
        var clip2_part2 = timeline.Clips.FirstOrDefault(c =>
            c.TrackIndex == 1 && c.StartTimeMs == 5000);
        Assert.NotNull(clip2_part2);
        Assert.Equal(5000, clip2_part2.DurationMs); // 10000 - 5000

        // clip3 분할 확인 (1000-7000 → 1000-5000 + 5000-7000)
        Assert.Equal(4000, clip3.DurationMs); // 5000 - 1000
        var clip3_part2 = timeline.Clips.FirstOrDefault(c =>
            c.TrackIndex == 2 && c.StartTimeMs == 5000);
        Assert.NotNull(clip3_part2);
        Assert.Equal(2000, clip3_part2.DurationMs); // 7000 - 5000
    }

    [Fact]
    public void CutAllTracksAtTime_NoClipsAtTime_DoesNothing()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var clip1 = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 3000,
            TrackIndex = 0
        };
        var clip2 = new ClipModel
        {
            Id = 2,
            StartTimeMs = 5000,
            DurationMs = 3000,
            TrackIndex = 0
        };

        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);

        var razorTool = new RazorTool(timeline);

        // Act: 클립이 없는 4000ms에서 자르기 시도
        razorTool.CutAllTracksAtTime(4000);

        // Assert: 변경 없음
        Assert.Equal(2, timeline.Clips.Count);
    }
}

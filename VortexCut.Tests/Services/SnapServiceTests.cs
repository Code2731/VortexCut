using VortexCut.Core.Models;
using VortexCut.Core.Interfaces;
using VortexCut.UI.Services;
using VortexCut.UI.ViewModels;
using VortexCut.Interop.Services;
using Moq;
using Xunit;

namespace VortexCut.Tests.Services;

/// <summary>
/// SnapService 단위 테스트
/// </summary>
public class SnapServiceTests
{
    private TimelineViewModel CreateTimelineViewModel()
    {
        var mockRenderService = new Mock<IRenderService>();
        var timelineService = new TimelineService();
        var projectService = new ProjectService(mockRenderService.Object, timelineService);
        return new TimelineViewModel(projectService);
    }

    [Fact]
    public void GetSnapTarget_SnapDisabled_ReturnsOriginalTime()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        timeline.SnapEnabled = false;
        var snapService = new SnapService(timeline);

        // Act
        var result = snapService.GetSnapTarget(5000);

        // Assert
        Assert.Equal(5000, result.TimeMs);
        Assert.False(result.Snapped);
    }

    [Fact]
    public void GetSnapTarget_NearClipEnd_SnapsToClipEnd()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        timeline.SnapEnabled = true;
        timeline.SnapThresholdMs = 100;

        var clip1 = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000, // EndTimeMs = 5000
            TrackIndex = 0
        };
        timeline.Clips.Add(clip1);

        var snapService = new SnapService(timeline);

        // Act: 클립 끝에서 80ms 떨어진 위치 (5080ms)
        var result = snapService.GetSnapTarget(5080);

        // Assert: 클립 끝(5000ms)에 Snap되어야 함
        Assert.Equal(5000, result.TimeMs);
        Assert.True(result.Snapped);
    }

    [Fact]
    public void GetSnapTarget_FarFromClipEnd_NoSnap()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        timeline.SnapEnabled = true;
        timeline.SnapThresholdMs = 100;

        var clip1 = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 0
        };
        timeline.Clips.Add(clip1);

        var snapService = new SnapService(timeline);

        // Act: 클립 끝에서 200ms 떨어진 위치 (임계값 100ms 초과)
        var result = snapService.GetSnapTarget(5200);

        // Assert: Snap 안 됨
        Assert.Equal(5200, result.TimeMs);
        Assert.False(result.Snapped);
    }

    [Fact]
    public void GetSnapTarget_NearPlayhead_SnapsToPlayhead()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        timeline.SnapEnabled = true;
        timeline.SnapThresholdMs = 100;
        timeline.CurrentTimeMs = 3000;

        var snapService = new SnapService(timeline);

        // Act: Playhead에서 50ms 떨어진 위치
        var result = snapService.GetSnapTarget(3050);

        // Assert: Playhead(3000ms)에 Snap
        Assert.Equal(3000, result.TimeMs);
        Assert.True(result.Snapped);
    }

    [Fact]
    public void GetSnapTarget_ExcludeClip_IgnoresExcludedClip()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        timeline.SnapEnabled = true;
        timeline.SnapThresholdMs = 100;

        var clip1 = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 0
        };
        var clip2 = new ClipModel
        {
            Id = 2,
            StartTimeMs = 6000,
            DurationMs = 3000,
            TrackIndex = 0
        };
        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);

        var snapService = new SnapService(timeline);

        // Act: clip1을 드래그 중이므로 제외, clip2 시작(6000ms)에 Snap
        var result = snapService.GetSnapTarget(6050, excludeClip: clip1);

        // Assert
        Assert.Equal(6000, result.TimeMs);
        Assert.True(result.Snapped);
    }

    [Fact]
    public void GetAllSnapPoints_ReturnsAllSnapCandidates()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();
        timeline.CurrentTimeMs = 1000;

        var clip1 = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000, // End: 5000
            TrackIndex = 0
        };
        var clip2 = new ClipModel
        {
            Id = 2,
            StartTimeMs = 6000,
            DurationMs = 3000, // End: 9000
            TrackIndex = 0
        };
        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);

        var snapService = new SnapService(timeline);

        // Act
        var snapPoints = snapService.GetAllSnapPoints();

        // Assert: Playhead(1000) + clip1(0, 5000) + clip2(6000, 9000) = 5개
        Assert.Contains(1000L, snapPoints); // Playhead
        Assert.Contains(0L, snapPoints);    // clip1 start
        Assert.Contains(5000L, snapPoints); // clip1 end
        Assert.Contains(6000L, snapPoints); // clip2 start
        Assert.Contains(9000L, snapPoints); // clip2 end
        Assert.Equal(5, snapPoints.Count);
    }
}

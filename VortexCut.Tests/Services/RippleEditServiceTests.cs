using VortexCut.Core.Models;
using VortexCut.Core.Interfaces;
using VortexCut.UI.Services;
using VortexCut.UI.ViewModels;
using VortexCut.Interop.Services;
using Moq;
using Xunit;

namespace VortexCut.Tests.Services;

/// <summary>
/// RippleEditService 단위 테스트
/// </summary>
public class RippleEditServiceTests
{
    private TimelineViewModel CreateTimelineViewModel()
    {
        var mockRenderService = new Mock<IRenderService>();
        var timelineService = new TimelineService();
        var projectService = new ProjectService(mockRenderService.Object, timelineService);
        return new TimelineViewModel(projectService);
    }

    [Fact]
    public void RippleDelete_RemovesClipAndShiftsFollowing()
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
            StartTimeMs = 3000,
            DurationMs = 2000, // End: 5000
            TrackIndex = 0
        };
        var clip3 = new ClipModel
        {
            Id = 3,
            StartTimeMs = 5000,
            DurationMs = 4000, // End: 9000
            TrackIndex = 0
        };

        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);
        timeline.Clips.Add(clip3);

        var rippleService = new RippleEditService(timeline);

        // Act: clip2 삭제 (3000-5000, 길이 2000ms)
        rippleService.RippleDelete(clip2);

        // Assert: clip2 제거되고, clip3이 2000ms 왼쪽으로 이동
        Assert.Equal(2, timeline.Clips.Count);
        Assert.DoesNotContain(clip2, timeline.Clips);

        // clip1은 그대로
        Assert.Equal(0, clip1.StartTimeMs);

        // clip3은 3000ms로 이동 (5000 - 2000)
        Assert.Equal(3000, clip3.StartTimeMs);
    }

    [Fact]
    public void RippleDelete_DifferentTrack_DoesNotAffectOtherTracks()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

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
            StartTimeMs = 5000,
            DurationMs = 3000,
            TrackIndex = 0
        };
        var clip3 = new ClipModel
        {
            Id = 3,
            StartTimeMs = 6000,
            DurationMs = 2000,
            TrackIndex = 1 // 다른 트랙
        };

        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);
        timeline.Clips.Add(clip3);

        var rippleService = new RippleEditService(timeline);

        // Act: clip1 삭제 (트랙 0)
        rippleService.RippleDelete(clip1);

        // Assert
        Assert.Equal(2, timeline.Clips.Count);

        // clip2는 왼쪽으로 이동
        Assert.Equal(0, clip2.StartTimeMs); // 5000 - 5000

        // clip3 (다른 트랙)은 영향 없음
        Assert.Equal(6000, clip3.StartTimeMs);
    }

    [Fact]
    public void RippleMove_ShiftsFollowingClips()
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
            DurationMs = 2000,
            TrackIndex = 0
        };

        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);

        var rippleService = new RippleEditService(timeline);

        // Act: clip1을 1000ms로 이동 (원래 0ms)
        rippleService.RippleMove(clip1, 1000);

        // Assert
        Assert.Equal(1000, clip1.StartTimeMs);

        // clip2도 1000ms 이동 (5000 + 1000)
        Assert.Equal(6000, clip2.StartTimeMs);
    }

    [Fact]
    public void RippleMove_BackwardMove_ShiftsFollowingClipsBackward()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var clip1 = new ClipModel
        {
            Id = 1,
            StartTimeMs = 2000,
            DurationMs = 3000, // End: 5000
            TrackIndex = 0
        };
        var clip2 = new ClipModel
        {
            Id = 2,
            StartTimeMs = 6000,
            DurationMs = 2000,
            TrackIndex = 0
        };

        timeline.Clips.Add(clip1);
        timeline.Clips.Add(clip2);

        var rippleService = new RippleEditService(timeline);

        // Act: clip1을 0ms로 이동 (2000ms 왼쪽으로)
        rippleService.RippleMove(clip1, 0);

        // Assert
        Assert.Equal(0, clip1.StartTimeMs);

        // clip2도 2000ms 왼쪽 이동 (6000 - 2000)
        Assert.Equal(4000, clip2.StartTimeMs);
    }

    [Fact]
    public void RippleInsert_InsertsAndShiftsFollowing()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var existingClip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 5000,
            DurationMs = 3000,
            TrackIndex = 0
        };
        timeline.Clips.Add(existingClip);

        var newClip = new ClipModel
        {
            Id = 2,
            FilePath = "new.mp4",
            DurationMs = 2000,
            TrackIndex = -1 // 아직 배치 안 됨
        };

        var rippleService = new RippleEditService(timeline);

        // Act: 3000ms 위치에 삽입
        rippleService.RippleInsert(newClip, 3000, 0);

        // Assert
        Assert.Equal(2, timeline.Clips.Count);
        Assert.Contains(newClip, timeline.Clips);

        // newClip 위치 확인
        Assert.Equal(3000, newClip.StartTimeMs);
        Assert.Equal(0, newClip.TrackIndex);

        // existingClip이 오른쪽으로 이동 (5000 + 2000)
        Assert.Equal(7000, existingClip.StartTimeMs);
    }

    [Fact]
    public void RollingEdit_AdjustsBoundary()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var leftClip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000, // End: 5000
            TrackIndex = 0
        };
        var rightClip = new ClipModel
        {
            Id = 2,
            StartTimeMs = 5000,
            DurationMs = 5000, // End: 10000
            TrackIndex = 0
        };

        timeline.Clips.Add(leftClip);
        timeline.Clips.Add(rightClip);

        var rippleService = new RippleEditService(timeline);

        // Act: 경계를 6000ms로 이동 (+1000ms)
        rippleService.RollingEdit(leftClip, rightClip, 6000);

        // Assert
        // leftClip: 0-6000
        Assert.Equal(0, leftClip.StartTimeMs);
        Assert.Equal(6000, leftClip.DurationMs);

        // rightClip: 6000-10000
        Assert.Equal(6000, rightClip.StartTimeMs);
        Assert.Equal(4000, rightClip.DurationMs);
    }

    [Fact]
    public void RollingEdit_NonAdjacentClips_ThrowsException()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var leftClip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 0
        };
        var rightClip = new ClipModel
        {
            Id = 2,
            StartTimeMs = 6000, // 간격 있음
            DurationMs = 5000,
            TrackIndex = 0
        };

        timeline.Clips.Add(leftClip);
        timeline.Clips.Add(rightClip);

        var rippleService = new RippleEditService(timeline);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            rippleService.RollingEdit(leftClip, rightClip, 6000));
    }

    [Fact]
    public void RollingEdit_TooShort_ThrowsException()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var leftClip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 0
        };
        var rightClip = new ClipModel
        {
            Id = 2,
            StartTimeMs = 5000,
            DurationMs = 5000,
            TrackIndex = 0
        };

        timeline.Clips.Add(leftClip);
        timeline.Clips.Add(rightClip);

        var rippleService = new RippleEditService(timeline);

        // Act & Assert: leftClip이 50ms가 되도록 시도 (최소 100ms 미만)
        Assert.Throws<InvalidOperationException>(() =>
            rippleService.RollingEdit(leftClip, rightClip, 50));
    }
}

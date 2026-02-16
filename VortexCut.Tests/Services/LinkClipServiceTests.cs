using VortexCut.Core.Models;
using VortexCut.Core.Interfaces;
using VortexCut.UI.Services;
using VortexCut.UI.ViewModels;
using VortexCut.Interop.Services;
using Moq;
using Xunit;

namespace VortexCut.Tests.Services;

/// <summary>
/// LinkClipService 단위 테스트
/// </summary>
public class LinkClipServiceTests
{
    private TimelineViewModel CreateTimelineViewModel()
    {
        var mockRenderService = new Mock<IRenderService>();
        var timelineService = new TimelineService();
        var projectService = new ProjectService(mockRenderService.Object, timelineService);
        return new TimelineViewModel(projectService);
    }

    [Fact]
    public void LinkClips_LinksVideoAndAudio()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel
        {
            Id = 1,
            FilePath = "video.mp4",
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 0
        };
        var audioClip = new ClipModel
        {
            Id = 2,
            FilePath = "video.mp4",
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 1
        };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);

        // Act
        linkService.LinkClips(videoClip, audioClip);

        // Assert
        Assert.True(videoClip.IsLinked);
        Assert.True(audioClip.IsLinked);
        Assert.Equal(audioClip.Id, videoClip.LinkedAudioClipId);
        Assert.Equal(videoClip.Id, audioClip.LinkedVideoClipId);
    }

    [Fact]
    public void UnlinkClip_RemovesLink()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel { Id = 1, TrackIndex = 0 };
        var audioClip = new ClipModel { Id = 2, TrackIndex = 1 };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);
        linkService.LinkClips(videoClip, audioClip);

        // Act
        linkService.UnlinkClip(videoClip);

        // Assert
        Assert.False(videoClip.IsLinked);
        Assert.False(audioClip.IsLinked);
        Assert.Null(videoClip.LinkedAudioClipId);
        Assert.Null(audioClip.LinkedVideoClipId);
    }

    [Fact]
    public void MoveLinkedClips_MovesBothClips()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 0
        };
        var audioClip = new ClipModel
        {
            Id = 2,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 1
        };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);
        linkService.LinkClips(videoClip, audioClip);

        // Act: 1000ms 이동
        linkService.MoveLinkedClips(videoClip, 1000);

        // Assert
        Assert.Equal(1000, videoClip.StartTimeMs);
        Assert.Equal(1000, audioClip.StartTimeMs);
    }

    [Fact]
    public void MoveLinkedClips_UnlinkedClip_MovesOnlyOne()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel
        {
            Id = 1,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 0
        };
        var audioClip = new ClipModel
        {
            Id = 2,
            StartTimeMs = 0,
            DurationMs = 5000,
            TrackIndex = 1
        };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);
        // 링크하지 않음

        // Act
        linkService.MoveLinkedClips(videoClip, 1000);

        // Assert
        Assert.Equal(1000, videoClip.StartTimeMs);
        Assert.Equal(0, audioClip.StartTimeMs); // 변경 없음
    }

    [Fact]
    public void DeleteLinkedClips_DeletesBothClips()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel { Id = 1, TrackIndex = 0 };
        var audioClip = new ClipModel { Id = 2, TrackIndex = 1 };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);
        linkService.LinkClips(videoClip, audioClip);

        // Act
        linkService.DeleteLinkedClips(videoClip);

        // Assert
        Assert.Empty(timeline.Clips);
    }

    [Fact]
    public void DeleteLinkedClips_UnlinkedClip_DeletesOnlyOne()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel { Id = 1, TrackIndex = 0 };
        var audioClip = new ClipModel { Id = 2, TrackIndex = 1 };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);
        // 링크하지 않음

        // Act
        linkService.DeleteLinkedClips(videoClip);

        // Assert
        Assert.Single(timeline.Clips);
        Assert.Contains(audioClip, timeline.Clips);
    }

    [Fact]
    public void GetLinkedClip_ReturnsLinkedClip()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel { Id = 1, TrackIndex = 0 };
        var audioClip = new ClipModel { Id = 2, TrackIndex = 1 };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);
        linkService.LinkClips(videoClip, audioClip);

        // Act
        var linked = linkService.GetLinkedClip(videoClip);

        // Assert
        Assert.NotNull(linked);
        Assert.Equal(audioClip.Id, linked.Id);
    }

    [Fact]
    public void GetLinkedClip_UnlinkedClip_ReturnsNull()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel { Id = 1, TrackIndex = 0 };
        timeline.Clips.Add(videoClip);

        var linkService = new LinkClipService(timeline);

        // Act
        var linked = linkService.GetLinkedClip(videoClip);

        // Assert
        Assert.Null(linked);
    }

    [Fact]
    public void AutoLinkClipsFromSameFile_LinksTwoClips()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var videoClip = new ClipModel
        {
            Id = 1,
            FilePath = "video.mp4",
            StartTimeMs = 5000,
            TrackIndex = 0
        };
        var audioClip = new ClipModel
        {
            Id = 2,
            FilePath = "video.mp4",
            StartTimeMs = 5000,
            TrackIndex = 1
        };

        timeline.Clips.Add(videoClip);
        timeline.Clips.Add(audioClip);

        var linkService = new LinkClipService(timeline);

        // Act
        linkService.AutoLinkClipsFromSameFile("video.mp4", 5000);

        // Assert
        Assert.True(videoClip.IsLinked);
        Assert.True(audioClip.IsLinked);
    }

    [Fact]
    public void LinkClips_AlreadyLinked_UnlinksAndRelinks()
    {
        // Arrange
        var timeline = CreateTimelineViewModel();

        var video1 = new ClipModel { Id = 1, TrackIndex = 0 };
        var audio1 = new ClipModel { Id = 2, TrackIndex = 1 };
        var audio2 = new ClipModel { Id = 3, TrackIndex = 1 };

        timeline.Clips.Add(video1);
        timeline.Clips.Add(audio1);
        timeline.Clips.Add(audio2);

        var linkService = new LinkClipService(timeline);

        // 첫 번째 링크
        linkService.LinkClips(video1, audio1);

        // Act: 다른 오디오 클립과 재링크
        linkService.LinkClips(video1, audio2);

        // Assert
        Assert.False(audio1.IsLinked); // 이전 링크 해제
        Assert.True(video1.IsLinked);
        Assert.True(audio2.IsLinked);
        Assert.Equal(audio2.Id, video1.LinkedAudioClipId);
    }
}

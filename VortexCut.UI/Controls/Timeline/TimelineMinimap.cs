using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 타임라인 미니맵 (전체 타임라인 축소 뷰)
/// </summary>
public class TimelineMinimap : Control
{
    private TimelineViewModel? _viewModel;
    private const double MinimapHeight = 20;
    private double _pixelsPerMs = 0.01; // 전체 타임라인을 맞추기 위한 스케일

    public TimelineMinimap()
    {
        Height = MinimapHeight;
    }

    public void SetViewModel(TimelineViewModel viewModel)
    {
        _viewModel = viewModel;

        // 클립 컬렉션 변경 시 갱신
        _viewModel.Clips.CollectionChanged += (s, e) => InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 프로페셔널 그라디언트 배경 (DaVinci Resolve 스타일)
        var backgroundGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromRgb(35, 35, 37), 0),
                new GradientStop(Color.FromRgb(25, 25, 27), 1)
            }
        };
        context.FillRectangle(backgroundGradient, new Rect(0, 0, Bounds.Width, MinimapHeight));

        if (_viewModel == null || _viewModel.Clips.Count == 0)
            return;

        // 전체 타임라인 길이 계산
        var totalDuration = _viewModel.Clips
            .Select(c => c.EndTimeMs)
            .DefaultIfEmpty(0)
            .Max();

        if (totalDuration == 0)
            return;

        // 미니맵 스케일 계산 (전체 타임라인을 Bounds.Width에 맞춤)
        _pixelsPerMs = Bounds.Width / totalDuration;

        // 모든 클립 렌더링 (타입별 색상)
        foreach (var clip in _viewModel.Clips)
        {
            var x = clip.StartTimeMs * _pixelsPerMs;
            var width = clip.DurationMs * _pixelsPerMs;
            var y = clip.TrackIndex * 2.0; // 트랙별로 2px씩 오프셋
            var height = 2.5;

            if (y + height > MinimapHeight - 1)
                y = MinimapHeight - height - 1;

            var rect = new Rect(x, y, Math.Max(width, 1), height);

            // 트랙 타입 확인
            var track = _viewModel.VideoTracks.FirstOrDefault(t => t.Index == clip.TrackIndex)
                        ?? (Core.Models.TrackModel?)_viewModel.AudioTracks.FirstOrDefault(t => t.Index == clip.TrackIndex);

            // 타입별 색상 (비디오: 파란색, 오디오: 초록색)
            Brush clipBrush;
            if (track?.Type == Core.Models.TrackType.Audio)
            {
                clipBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100)); // 밝은 초록
            }
            else
            {
                clipBrush = new SolidColorBrush(Color.FromRgb(100, 150, 220)); // 밝은 파란색
            }

            context.FillRectangle(clipBrush, rect);
        }

        // 현재 뷰포트 하이라이트 (프로페셔널 스타일)
        var viewportRect = CalculateViewportRect();

        // 뷰포트 내부 반투명 오버레이
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            viewportRect);

        // 뷰포트 테두리 (글로우 효과)
        var viewportPen = new Pen(
            new SolidColorBrush(Color.FromRgb(255, 200, 80)),
            2);
        context.DrawRectangle(viewportPen, viewportRect);

        // Playhead 표시 (더 선명한 빨간 선 + 그림자)
        var playheadX = _viewModel.CurrentTimeMs * _pixelsPerMs;

        // Playhead 그림자
        var shadowPen = new Pen(
            new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
            2);
        context.DrawLine(shadowPen,
            new Point(playheadX + 1, 0),
            new Point(playheadX + 1, MinimapHeight));

        // Playhead 본체
        var playheadPen = new Pen(
            new SolidColorBrush(Color.FromRgb(255, 60, 60)),
            2);
        context.DrawLine(playheadPen,
            new Point(playheadX, 0),
            new Point(playheadX, MinimapHeight));
    }

    /// <summary>
    /// 현재 뷰포트 사각형 계산
    /// </summary>
    private Rect CalculateViewportRect()
    {
        if (_viewModel == null)
            return new Rect();

        // TODO: 실제 스크롤 오프셋과 줌 레벨을 사용해서 계산
        // 현재는 전체 뷰포트로 가정
        var viewportWidth = Bounds.Width * 0.3; // 전체의 30%
        var viewportX = _viewModel.CurrentTimeMs * _pixelsPerMs - viewportWidth / 2;
        viewportX = Math.Clamp(viewportX, 0, Bounds.Width - viewportWidth);

        return new Rect(viewportX, 0, viewportWidth, MinimapHeight);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_viewModel == null)
            return;

        // 클릭 위치로 Playhead 이동
        var clickX = e.GetPosition(this).X;
        var timeMs = (long)(clickX / _pixelsPerMs);

        // 타임라인 총 길이 내로 제한
        var maxTime = _viewModel.Clips
            .Select(c => c.EndTimeMs)
            .DefaultIfEmpty(0)
            .Max();

        var clampedTime = Math.Clamp(timeMs, 0, maxTime);
        _viewModel.CurrentTimeMs = clampedTime;
        _viewModel.OnScrubRequested?.Invoke(clampedTime);

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // 드래그 중이면 Playhead 계속 업데이트
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (_viewModel == null)
                return;

            var currentX = e.GetPosition(this).X;
            var timeMs = (long)(currentX / _pixelsPerMs);

            var maxTime = _viewModel.Clips
                .Select(c => c.EndTimeMs)
                .DefaultIfEmpty(0)
                .Max();

            var clampedTime = Math.Clamp(timeMs, 0, maxTime);
            _viewModel.CurrentTimeMs = clampedTime;
            _viewModel.OnScrubRequested?.Invoke(clampedTime);

            e.Handled = true;
            InvalidateVisual();
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VortexCut.Core.Models;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 개별 트랙 헤더 컨트롤
/// </summary>
public partial class TrackHeaderControl : UserControl
{
    public static readonly StyledProperty<TrackModel?> TrackProperty =
        AvaloniaProperty.Register<TrackHeaderControl, TrackModel?>(nameof(Track));

    public TrackModel? Track
    {
        get => GetValue(TrackProperty);
        set
        {
            SetValue(TrackProperty, value);
            UpdateTrackIcon();
        }
    }

    private Border? _resizeGrip;
    private Border? _trackBadge;
    private TextBlock? _trackTypeIcon;
    private bool _isResizing;
    private Point _resizeStartPoint;
    private double _originalHeight;

    public TrackHeaderControl()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void UpdateTrackIcon()
    {
        if (Track != null && _trackTypeIcon != null && _trackBadge != null)
        {
            // 트랙 타입에 따라 아이콘 및 색상 설정
            if (Track.Type == TrackType.Video)
            {
                _trackTypeIcon.Text = "🎬"; // 비디오 아이콘
                _trackBadge.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(0, 122, 204)); // 파란색
            }
            else // Audio
            {
                _trackTypeIcon.Text = "🔊"; // 오디오 아이콘
                _trackBadge.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(92, 184, 92)); // 초록색
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _resizeGrip = this.FindControl<Border>("ResizeGrip");
        _trackBadge = this.FindControl<Border>("TrackBadge");
        _trackTypeIcon = this.FindControl<TextBlock>("TrackTypeIcon");

        if (_resizeGrip != null)
        {
            _resizeGrip.PointerPressed += OnResizeGripPressed;
            _resizeGrip.PointerMoved += OnResizeGripMoved;
            _resizeGrip.PointerReleased += OnResizeGripReleased;
        }
    }

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Track == null) return;

        _isResizing = true;
        _resizeStartPoint = e.GetPosition(this);
        _originalHeight = Track.Height;
        e.Handled = true;
    }

    private void OnResizeGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing || Track == null) return;

        var currentPoint = e.GetPosition(this);
        var deltaY = currentPoint.Y - _resizeStartPoint.Y;

        // 트랙 높이 조절 (30~200px)
        Track.Height = Math.Clamp(_originalHeight + deltaY, 30, 200);
        e.Handled = true;
    }

    private void OnResizeGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isResizing = false;
        e.Handled = true;
    }
}

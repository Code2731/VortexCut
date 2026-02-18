using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// 개별 트랙 헤더 컨트롤 (DaVinci Resolve 스타일)
/// - 좌측 색상 바 + 트랙 이름 + M/S/L 토글 버튼
/// - 더블클릭 트랙 이름 편집
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
            UpdateDisplay();
        }
    }

    // DaVinci Resolve 스타일 16색 팔레트
    private static readonly uint[] ColorPalette =
    {
        0xFFE74C3C, 0xFFE67E22, 0xFFF1C40F, 0xFF2ECC71,
        0xFF1ABC9C, 0xFF3498DB, 0xFF9B59B6, 0xFFE91E63,
        0xFF795548, 0xFF95A5A6, 0xFF808000, 0xFF008080,
        0xFF1A237E, 0xFF880E4F, 0xFFFF7043, 0xFFECEFF1,
    };

    private Border? _resizeGrip;
    private Border? _colorBar;
    private Border? _topHighlight;
    private TextBlock? _trackNameLabel;
    private TextBox? _trackNameEditor;
    private Border? _displayModeButton;
    private TextBlock? _displayModeLabel;
    private Avalonia.Controls.Primitives.Popup? _colorPopup;
    private Avalonia.Controls.Primitives.UniformGrid? _colorGrid;
    private bool _isResizing;
    private bool _isEditing;
    private Point _resizeStartPoint;
    private double _originalHeight;

    public TrackHeaderControl()
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>
    /// 트랙 정보로 UI 업데이트 (이름, 색상 바)
    /// </summary>
    private void UpdateDisplay()
    {
        if (Track == null) return;

        var color = ArgbToColor(Track.ColorArgb);

        // 트랙 이름 (트랙 색상으로 표시)
        if (_trackNameLabel != null)
        {
            _trackNameLabel.Text = Track.Name;
            _trackNameLabel.Foreground = new SolidColorBrush(color);
        }

        // 좌측 색상 바
        if (_colorBar != null)
            _colorBar.Background = new SolidColorBrush(color);

        // 상단 하이라이트 (트랙 색상)
        if (_topHighlight != null)
            _topHighlight.Background = new SolidColorBrush(color);
    }

    private static Color ArgbToColor(uint argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _resizeGrip = this.FindControl<Border>("ResizeGrip");
        _colorBar = this.FindControl<Border>("ColorBar");
        _topHighlight = this.FindControl<Border>("TopHighlight");
        _trackNameLabel = this.FindControl<TextBlock>("TrackNameLabel");
        _trackNameEditor = this.FindControl<TextBox>("TrackNameEditor");
        _displayModeButton = this.FindControl<Border>("DisplayModeButton");
        _displayModeLabel = this.FindControl<TextBlock>("DisplayModeLabel");
        _colorPopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("ColorPopup");
        _colorGrid = this.FindControl<Avalonia.Controls.Primitives.UniformGrid>("ColorGrid");

        // Display Mode 버튼 클릭 + 호버 효과
        if (_displayModeButton != null)
        {
            _displayModeButton.PointerPressed += OnDisplayModePressed;
            _displayModeButton.PointerEntered += (_, _) =>
            {
                _displayModeButton.Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x52));
                if (_displayModeLabel != null) _displayModeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            };
            _displayModeButton.PointerExited += (_, _) =>
            {
                _displayModeButton.Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x40));
                if (_displayModeLabel != null) _displayModeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            };
        }

        // 리사이즈 그립 이벤트
        if (_resizeGrip != null)
        {
            _resizeGrip.PointerPressed += OnResizeGripPressed;
            _resizeGrip.PointerMoved += OnResizeGripMoved;
            _resizeGrip.PointerReleased += OnResizeGripReleased;
        }

        // 색상 바 우클릭 → 색상 팔레트
        if (_colorBar != null)
        {
            _colorBar.PointerPressed += OnColorBarPressed;
        }

        // 색상 팔레트 초기화
        BuildColorPalette();

        // 트랙 이름 더블클릭 → 편집 모드
        if (_trackNameLabel != null)
        {
            _trackNameLabel.DoubleTapped += OnTrackNameDoubleTapped;
        }

        // 편집 완료 이벤트
        if (_trackNameEditor != null)
        {
            _trackNameEditor.KeyDown += OnEditorKeyDown;
            _trackNameEditor.LostFocus += OnEditorLostFocus;
        }

        // M 버튼 토글 → Rust 트랙 뮤트 동기화
        var muteButton = this.FindControl<ToggleButton>("MuteButton");
        if (muteButton != null)
        {
            muteButton.IsCheckedChanged += OnMuteToggled;
        }

        // A 버튼 토글 → Armed 상호배타 처리
        var armButton = this.FindControl<ToggleButton>("ArmButton");
        if (armButton != null)
        {
            armButton.IsCheckedChanged += OnArmToggled;
        }
    }

    private void OnMuteToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Track == null) return;

        // MainViewModel → ProjectService 경로로 Rust에 뮤트 동기화
        var mainWindow = TopLevel.GetTopLevel(this);
        if (mainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.Timeline.ProjectServiceRef.SetTrackMuted(Track.Id, Track.IsMuted);

            // 자막 트랙 뮤트 시 미리보기 자막 즉시 갱신
            if (Track.Type == TrackType.Subtitle)
            {
                mainVm.Preview.RefreshSubtitleOverlay();
            }
        }
    }

    /// <summary>
    /// Armed 토글: 같은 타입 트랙 중 1개만 armed 가능
    /// </summary>
    private void OnArmToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Track == null || !Track.IsArmed) return;

        // 같은 타입의 다른 트랙 armed 해제
        var mainWindow = TopLevel.GetTopLevel(this);
        if (mainWindow?.DataContext is MainViewModel mainVm)
        {
            var tracks = Track.Type == TrackType.Video
                ? mainVm.Timeline.VideoTracks
                : Track.Type == TrackType.Audio
                    ? mainVm.Timeline.AudioTracks
                    : mainVm.Timeline.SubtitleTracks;

            foreach (var t in tracks)
            {
                if (t != Track && t.IsArmed)
                    t.IsArmed = false;
            }
        }
    }

    // === Display Mode 순환 ===

    private void OnDisplayModePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Track == null) return;

        // Filmstrip → Thumbnail → Minimal → Filmstrip 순환
        Track.DisplayMode = Track.DisplayMode switch
        {
            ClipDisplayMode.Filmstrip => ClipDisplayMode.Thumbnail,
            ClipDisplayMode.Thumbnail => ClipDisplayMode.Minimal,
            ClipDisplayMode.Minimal => ClipDisplayMode.Filmstrip,
            _ => ClipDisplayMode.Filmstrip
        };

        UpdateDisplayModeLabel();
        e.Handled = true;
    }

    private void UpdateDisplayModeLabel()
    {
        if (_displayModeLabel == null || Track == null) return;

        _displayModeLabel.Text = Track.DisplayMode switch
        {
            ClipDisplayMode.Filmstrip => "FILM",
            ClipDisplayMode.Thumbnail => "THUMB",
            ClipDisplayMode.Minimal => "MIN",
            _ => "FILM"
        };
    }

    // === 색상 팔레트 ===

    private void BuildColorPalette()
    {
        if (_colorGrid == null) return;

        foreach (var argb in ColorPalette)
        {
            var color = ArgbToColor(argb);
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            swatch.PointerPressed += (s, e) =>
            {
                if (Track == null) return;
                Track.ColorArgb = argb;
                UpdateDisplay();
                if (_colorPopup != null) _colorPopup.IsOpen = false;
            };
            _colorGrid.Children.Add(swatch);
        }
    }

    private void OnColorBarPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed && _colorPopup != null)
        {
            _colorPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    // === 트랙 이름 편집 ===

    private void OnTrackNameDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Track == null) return;
        StartEditing();
    }

    private void StartEditing()
    {
        if (_trackNameLabel == null || _trackNameEditor == null || Track == null) return;

        _isEditing = true;
        _trackNameLabel.IsVisible = false;
        _trackNameEditor.IsVisible = true;
        _trackNameEditor.Text = Track.Name;
        _trackNameEditor.SelectAll();
        _trackNameEditor.Focus();
    }

    private void CommitEdit()
    {
        if (!_isEditing || _trackNameEditor == null || _trackNameLabel == null || Track == null) return;

        _isEditing = false;
        var newName = _trackNameEditor.Text?.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            Track.Name = newName;
            _trackNameLabel.Text = newName;
        }

        _trackNameEditor.IsVisible = false;
        _trackNameLabel.IsVisible = true;
    }

    private void CancelEdit()
    {
        if (!_isEditing || _trackNameEditor == null || _trackNameLabel == null) return;

        _isEditing = false;
        _trackNameEditor.IsVisible = false;
        _trackNameLabel.IsVisible = true;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void OnEditorLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CommitEdit();
    }

    // === 리사이즈 그립 ===

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

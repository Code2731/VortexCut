using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Views;

public partial class SourceMonitorView : UserControl
{
    private Slider? _scrubSlider;
    private bool _isUserScrubbing;

    public SourceMonitorView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SetupScrubSlider();
    }

    private void SetupScrubSlider()
    {
        if (_scrubSlider != null) return;

        _scrubSlider = this.FindControl<Slider>("ScrubSlider");
        if (_scrubSlider == null) return;

        // Slider 드래그 시 스크럽 렌더링
        _scrubSlider.AddHandler(PointerPressedEvent, OnSliderPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _scrubSlider.AddHandler(PointerReleasedEvent, OnSliderPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _scrubSlider.AddHandler(PointerMovedEvent, OnSliderPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Slider 값 변경 시 (클릭으로 점프)
        _scrubSlider.ValueChanged += OnSliderValueChanged;
    }

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isUserScrubbing = true;

        // 재생 중이면 정지 → 스크럽 가능하게
        var vm = GetSourceMonitorViewModel();
        if (vm != null && vm.IsPlaying)
            vm.TogglePlayback();
    }

    private void OnSliderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isUserScrubbing || _scrubSlider == null) return;

        var vm = GetSourceMonitorViewModel();
        if (vm == null) return;

        // TwoWay 바인딩이 CurrentTimeMs를 갱신하므로 슬라이더 값 사용
        vm.RenderFrameAsync((long)_scrubSlider.Value);
    }

    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isUserScrubbing || _scrubSlider == null)
        {
            _isUserScrubbing = false;
            return;
        }

        _isUserScrubbing = false;

        var vm = GetSourceMonitorViewModel();
        if (vm == null) return;

        // 최종 위치 렌더링
        vm.RenderFrameAsync((long)_scrubSlider.Value);
    }

    private void OnSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        // 사용자 드래그 중이거나 재생 중이면 무시
        if (_isUserScrubbing) return;

        var vm = GetSourceMonitorViewModel();
        if (vm == null || vm.IsPlaying) return;

        // Slider 클릭으로 값이 바뀐 경우 스크럽
        vm.RenderFrameAsync((long)e.NewValue);
    }

    private SourceMonitorViewModel? GetSourceMonitorViewModel()
    {
        return (DataContext as MainViewModel)?.SourceMonitor;
    }
}

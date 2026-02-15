using Avalonia.Controls;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Views;

public partial class TimelinePanelView : UserControl
{
    private Controls.TimelineCanvas? _timelineCanvas;
    private TimelineViewModel? _timelineVm;

    public TimelinePanelView()
    {
        InitializeComponent();

        // DataContext가 변경되면 TimelineCanvas에 ViewModel 설정
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // 기존 구독 해제
        if (_timelineVm != null)
        {
            _timelineVm.PropertyChanged -= OnTimelineVmPropertyChanged;
            _timelineVm.RequestZoomFit = null;
        }

        if (DataContext is MainViewModel viewModel)
        {
            _timelineCanvas = this.FindControl<Controls.TimelineCanvas>("TimelineCanvas");
            _timelineVm = viewModel.Timeline;

            if (_timelineCanvas != null)
            {
                _timelineCanvas.ViewModel = _timelineVm;
                System.Diagnostics.Debug.WriteLine("✅ TimelineCanvas ViewModel set successfully");
            }

            // ZoomLevel 변경 감지 → TimelineCanvas 동기화
            if (_timelineVm != null)
            {
                _timelineVm.PropertyChanged += OnTimelineVmPropertyChanged;
                _timelineVm.RequestZoomFit = () =>
                {
                    _timelineCanvas?.FitZoom();
                };
            }
        }
    }

    private void OnTimelineVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.ZoomLevel) && _timelineCanvas != null && _timelineVm != null)
        {
            // ZoomLevel(1.0=100%) → pixelsPerMs 변환: 기본값 0.1 * ZoomLevel
            var pixelsPerMs = 0.1 * _timelineVm.ZoomLevel;
            _timelineCanvas.SetZoom(pixelsPerMs);
        }
    }
}

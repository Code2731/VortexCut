using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Views;

public partial class ProjectBinView : UserControl
{
    private Point _dragStartPoint;
    private bool _isDragging;
    private bool _pointerDownOnItem;

    public ProjectBinView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Project Bin 미디어 아이템 더블클릭 → Clip Monitor에 로드
    /// (XAML에서 ListBox.DoubleTapped로 연결)
    /// </summary>
    private void OnMediaItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 드래그 중이면 무시
        if (_isDragging) return;

        var listBox = this.FindControl<ListBox>("MediaListBox");
        var mediaItem = listBox?.SelectedItem as MediaItem;

        // SelectedItem이 아직 안 설정된 경우 → 클릭된 요소에서 DataContext 탐색
        if (mediaItem == null && e.Source is Control source)
        {
            // 시각 트리를 올라가며 MediaItem DataContext 탐색
            var current = source;
            while (current != null)
            {
                if (current.DataContext is MediaItem item)
                {
                    mediaItem = item;
                    break;
                }
                current = current.Parent as Control;
            }
        }

        if (mediaItem == null) return;

        // MainViewModel을 찾아서 LoadClipToSourceMonitor 호출
        var mainWindow = TopLevel.GetTopLevel(this);
        if (mainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.LoadClipToSourceMonitor(mediaItem);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // ListBox 아이템 위에서만 드래그 준비
            var listBox = this.FindControl<ListBox>("MediaListBox");
            if (listBox?.SelectedItem is MediaItem)
            {
                _dragStartPoint = e.GetPosition(this);
                _isDragging = false;
                _pointerDownOnItem = true;
            }
            else
            {
                _pointerDownOnItem = false;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_pointerDownOnItem || _isDragging) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var currentPoint = e.GetPosition(this);
        var diff = currentPoint - _dragStartPoint;

        // 드래그 임계값: 15px (더블클릭 시 손 떨림으로 인한 오작동 방지)
        if (Math.Abs(diff.X) > 15 || Math.Abs(diff.Y) > 15)
        {
            var listBox = this.FindControl<ListBox>("MediaListBox");
            if (listBox?.SelectedItem is MediaItem mediaItem)
            {
                _isDragging = true;
#pragma warning disable CS0618 // DataObject/DoDragDrop deprecated in 11.3
                var dragData = new DataObject();
                dragData.Set("MediaItem", mediaItem);
                DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy);
#pragma warning restore CS0618
                _isDragging = false;
                _pointerDownOnItem = false;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
        _pointerDownOnItem = false;
    }
}

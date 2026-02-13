using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using VortexCut.Core.Models;

namespace VortexCut.UI.Views;

public partial class ProjectBinView : UserControl
{
    private Point _dragStartPoint;
    private bool _isDragging;

    public ProjectBinView()
    {
        InitializeComponent();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(this);
            _isDragging = false;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_isDragging)
        {
            var currentPoint = e.GetPosition(this);
            var diff = currentPoint - _dragStartPoint;

            // 최소 드래그 거리 확인
            if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
            {
                // ListBox에서 선택된 항목 가져오기
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
                }
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
    }
}

using Avalonia.Input;
using Avalonia.Threading;
using VortexCut.Core.Models;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// ClipCanvasPanel — Drag & Drop 처리 (미디어 아이템 드롭)
/// </summary>
public partial class ClipCanvasPanel
{
    private void HandleDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragEventArgs.Data deprecated in Avalonia 11.3
        if (e.Data.Contains("MediaItem"))
#pragma warning restore CS0618
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void HandleDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragEventArgs.Data deprecated in Avalonia 11.3
        if (e.Data.Contains("MediaItem") && _viewModel != null)
        {
            var mediaItem = e.Data.Get("MediaItem") as MediaItem;
#pragma warning restore CS0618
            if (mediaItem != null)
            {
                var dropPoint = e.GetPosition(this);

                // 드롭 위치를 타임라인 시간과 트랙으로 변환
                long startTimeMs = XToTime(dropPoint.X);
                int trackIndex = GetTrackIndexAtY(dropPoint.Y);

                // ViewModel을 통해 클립 추가
                Dispatcher.UIThread.Post(() =>
                {
                    _viewModel.AddClipFromMediaItem(mediaItem, startTimeMs, trackIndex);
                });
            }

            e.Handled = true;
        }
    }
}

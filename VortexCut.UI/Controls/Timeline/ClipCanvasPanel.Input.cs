using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Linq;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;
using VortexCut.UI.Services.Actions;

namespace VortexCut.UI.Controls.Timeline;

/// <summary>
/// ClipCanvasPanel — 마우스 입력 처리 (클릭, 드래그, 트림, 줌/팬)
/// </summary>
public partial class ClipCanvasPanel
{
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        _hoveredClip = null;

        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        // 중간 버튼: Pan 시작
        if (properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartPoint = point;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        // 왼쪽 버튼: Razor 모드 또는 클립 선택/드래그/트림
        if (properties.IsLeftButtonPressed)
        {
            // 1. 키프레임 HitTest (최우선)
            var (keyframe, keyframeSystem, clip) = GetKeyframeAtPosition(point);
            if (keyframe != null && keyframeSystem != null && clip != null)
            {
                _isDraggingKeyframe = true;
                _draggingKeyframe = keyframe;
                _draggingKeyframeSystem = keyframeSystem;
                _draggingKeyframeClip = clip;
                _dragStartPoint = point;
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
                return;
            }

            // 2. Razor 모드: 클립 자르기
            if (_viewModel != null && _viewModel.RazorModeEnabled)
            {
                var clickedClip = GetClipAtPosition(point);
                if (clickedClip != null && _viewModel.RazorTool != null)
                {
                    // Lock된 트랙 클립은 Razor 차단
                    var razorTrack = GetTrackByIndex(clickedClip.TrackIndex);
                    if (razorTrack != null && razorTrack.IsLocked)
                    {
                        e.Handled = true;
                        return;
                    }

                    var cutTime = XToTime(point.X);

                    // Shift + 클릭: 모든 트랙 동시 자르기
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        _viewModel.RazorTool.CutAllTracksAtTime(cutTime);
                    }
                    else
                    {
                        _viewModel.RazorTool.CutClipAtTime(clickedClip, cutTime);
                    }

                    InvalidateVisual();
                    e.Handled = true;
                }
                return;
            }

            // 재생 중이면 즉시 중지 (직접 IsPlaying 설정 + 콜백으로 타이머도 중지)
            if (_viewModel != null && _viewModel.IsPlaying)
            {
                _viewModel.IsPlaying = false;
                _viewModel.RequestStopPlayback?.Invoke();
            }

            // 일반 모드: 클립 선택/드래그/트림
            _selectedClip = GetClipAtPosition(point);

            if (_selectedClip != null)
            {
                // Ctrl + 클릭: 다중 선택
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _viewModel != null)
                {
                    if (_viewModel.SelectedClips.Contains(_selectedClip))
                    {
                        _viewModel.SelectedClips.Remove(_selectedClip);
                    }
                    else
                    {
                        _viewModel.SelectedClips.Add(_selectedClip);
                    }

                    // Ctrl+클릭: 마지막 선택/해제된 클립 또는 첫 번째 선택된 클립
                    _viewModel.SelectedClip = _viewModel.SelectedClips.Count > 0
                        ? _viewModel.SelectedClips[^1]
                        : null;

                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }

                // 단일 선택 (Ctrl 없음)
                if (_viewModel != null)
                {
                    _viewModel.SelectedClips.Clear();
                    _viewModel.SelectedClips.Add(_selectedClip);
                    _viewModel.SelectedClip = _selectedClip;
                }

                // Lock된 트랙의 클립은 드래그/트림 차단
                var selectedTrack = GetTrackByIndex(_selectedClip.TrackIndex);
                if (selectedTrack != null && selectedTrack.IsLocked)
                {
                    Cursor = new Cursor(StandardCursorType.No);
                }
                else
                {
                    // 트림 엣지 감지
                    _trimEdge = HitTestEdge(_selectedClip, point);

                    if (_trimEdge != ClipEdge.None)
                    {
                        // 트림 모드
                        _isTrimming = true;
                        _draggingClip = _selectedClip;
                        _dragStartPoint = point;
                        _originalStartTimeMs = _selectedClip.StartTimeMs;
                        _originalDurationMs = _selectedClip.DurationMs;
                        if (_viewModel != null) _viewModel.IsEditing = true;
                        Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    }
                    else
                    {
                        // 드래그 모드
                        _isDragging = true;
                        _draggingClip = _selectedClip;
                        _dragStartPoint = point;
                        _originalStartTimeMs = _selectedClip.StartTimeMs;
                        _originalTrackIndex = _selectedClip.TrackIndex;
                        if (_viewModel != null) _viewModel.IsEditing = true;
                    }
                }
            }
            else
            {
                // 빈 공간 클릭: 선택 해제 + Playhead 이동
                if (_viewModel != null)
                {
                    _viewModel.SelectedClips.Clear();
                    _viewModel.SelectedClip = null;
                    long clickedTimeMs = XToTime(point.X);
                    _viewModel.CurrentTimeMs = Math.Max(0, clickedTimeMs);
                }
            }

            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);

        // Razor 모드: Cross 커서
        if (_viewModel != null && _viewModel.RazorModeEnabled && !_isDragging && !_isTrimming && !_isPanning)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
        }
        else if (!_isDragging && !_isTrimming && !_isPanning)
        {
            Cursor = Cursor.Default;
        }

        // Pan 처리 (중간 버튼)
        if (_isPanning)
        {
            var delta = point - _panStartPoint;

            var timelineCanvas = this.GetVisualAncestors().OfType<TimelineCanvas>().FirstOrDefault();
            if (timelineCanvas != null)
            {
                var scrollViewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                if (scrollViewer != null)
                {
                    scrollViewer.Offset = new Vector(
                        Math.Max(0, scrollViewer.Offset.X - delta.X),
                        Math.Max(0, scrollViewer.Offset.Y - delta.Y)
                    );
                }
            }

            _panStartPoint = point;
            e.Handled = true;
            return;
        }

        // 키프레임 드래그 처리 (최우선)
        if (_isDraggingKeyframe && _draggingKeyframe != null && _draggingKeyframeSystem != null && _draggingKeyframeClip != null)
        {
            var deltaX = point.X - _dragStartPoint.X;
            var deltaTimeMs = (long)(deltaX / _pixelsPerMs);
            var newTime = Math.Max(0, _draggingKeyframe.Time + deltaTimeMs / 1000.0);

            var clipDurationSec = _draggingKeyframeClip.DurationMs / 1000.0;
            newTime = Math.Clamp(newTime, 0, clipDurationSec);

            _draggingKeyframeSystem.UpdateKeyframe(_draggingKeyframe, newTime, _draggingKeyframe.Value);

            _dragStartPoint = point;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // 트림 처리
        if (_isTrimming && _draggingClip != null)
        {
            var currentTime = XToTime(point.X);

            if (_trimEdge == ClipEdge.Left)
            {
                var newStartTime = Math.Max(0, currentTime);
                var maxStartTime = _originalStartTimeMs + _originalDurationMs - 100;

                newStartTime = Math.Min(newStartTime, maxStartTime);

                var deltaTime = newStartTime - _originalStartTimeMs;
                _draggingClip.StartTimeMs = newStartTime;
                _draggingClip.DurationMs = _originalDurationMs - deltaTime;
            }
            else if (_trimEdge == ClipEdge.Right)
            {
                var newEndTime = Math.Max(_draggingClip.StartTimeMs + 100, currentTime);
                _draggingClip.DurationMs = newEndTime - _draggingClip.StartTimeMs;
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // 클립 드래그 처리
        if (_isDragging && _draggingClip != null)
        {
            var deltaX = point.X - _dragStartPoint.X;

            var deltaTimeMs = (long)(deltaX / _pixelsPerMs);
            var newStartTime = Math.Max(0, _draggingClip.StartTimeMs + deltaTimeMs);

            // Snap 적용
            if (_snapService != null && _viewModel != null && _viewModel.SnapEnabled)
            {
                var snapResult = _snapService.GetSnapTarget(newStartTime, _draggingClip);
                _draggingClip.StartTimeMs = snapResult.TimeMs;
                _lastSnappedTimeMs = snapResult.Snapped ? snapResult.TimeMs : -1;
            }
            else
            {
                _draggingClip.StartTimeMs = newStartTime;
                _lastSnappedTimeMs = -1;
            }

            _dragStartPoint = point;
            InvalidateVisual();
        }

        // 호버 감지 (드래그/트림/팬 중이 아닐 때)
        if (!_isDragging && !_isTrimming && !_isPanning && !_isDraggingKeyframe)
        {
            var hoveredClip = GetClipAtPosition(point);
            if (hoveredClip != _hoveredClip)
            {
                _hoveredClip = hoveredClip;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Pan 종료
        if (_isPanning)
        {
            _isPanning = false;
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        // 키프레임 드래그 종료 (최우선)
        if (_isDraggingKeyframe)
        {
            _isDraggingKeyframe = false;
            _draggingKeyframe = null;
            _draggingKeyframeSystem = null;
            _draggingKeyframeClip = null;
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        // 트림 종료 → Undo 기록 + Rust 동기화
        if (_isTrimming)
        {
            if (_draggingClip != null && _viewModel != null)
            {
                if (_draggingClip.StartTimeMs != _originalStartTimeMs || _draggingClip.DurationMs != _originalDurationMs)
                {
                    _viewModel.ProjectServiceRef.SyncClipToRust(_draggingClip);

                    var trimAction = new TrimClipAction(
                        _draggingClip,
                        _originalStartTimeMs, _originalDurationMs,
                        _draggingClip.StartTimeMs, _draggingClip.DurationMs,
                        _viewModel.ProjectServiceRef);
                    _viewModel.UndoRedo.RecordAction(trimAction);
                }
            }

            _isTrimming = false;
            _trimEdge = ClipEdge.None;
            _draggingClip = null;
            if (_viewModel != null) _viewModel.IsEditing = false;
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        // 클립 드래그 종료 → Undo 기록 + Rust 동기화
        if (_isDragging && _draggingClip != null && _viewModel != null)
        {
            if (_draggingClip.StartTimeMs != _originalStartTimeMs || _draggingClip.TrackIndex != _originalTrackIndex)
            {
                _viewModel.ProjectServiceRef.SyncClipToRust(_draggingClip);

                var moveAction = new MoveClipAction(
                    _draggingClip,
                    _originalStartTimeMs, _originalTrackIndex,
                    _draggingClip.StartTimeMs, _draggingClip.TrackIndex,
                    _viewModel.ProjectServiceRef);
                _viewModel.UndoRedo.RecordAction(moveAction);
            }
        }

        _isDragging = false;
        _draggingClip = null;
        _lastSnappedTimeMs = -1;
        if (_viewModel != null) _viewModel.IsEditing = false;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Zoom/Pan 마우스 휠 처리
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl + 마우스휠: 수평 Zoom (0.001 ~ 5.0)
            var zoomFactor = e.Delta.Y > 0 ? 1.1 : 0.9;
            var newZoom = Math.Clamp(_pixelsPerMs * zoomFactor, 0.001, 5.0);

            var timelineCanvas = this.GetVisualAncestors().OfType<TimelineCanvas>().FirstOrDefault();
            if (timelineCanvas != null)
            {
                timelineCanvas.SetZoom(newZoom);
            }

            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Alt + 마우스휠: 수직 Zoom (트랙 높이 30~200px)
            var heightDelta = e.Delta.Y > 0 ? 10 : -10;

            var mousePos = e.GetPosition(this);
            var trackIndex = GetTrackIndexAtY(mousePos.Y);
            var track = GetTrackByIndex(trackIndex);

            if (track != null)
            {
                track.Height = Math.Clamp(track.Height + heightDelta, 30, 200);
                InvalidateVisual();

                var timelineCanvas = this.GetVisualAncestors().OfType<TimelineCanvas>().FirstOrDefault();
                if (timelineCanvas != null && _viewModel != null)
                {
                    // ViewModel 변경 감지로 자동 업데이트
                }
            }

            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift + 마우스휠: 수평 스크롤
        }
        else
        {
            // 마우스휠 기본: 수직 스크롤
        }
    }

    private ClipModel? GetClipAtPosition(Point point)
    {
        foreach (var clip in _clips)
        {
            double x = TimeToX(clip.StartTimeMs);
            double width = DurationToWidth(clip.DurationMs);
            double y = GetTrackYPosition(clip.TrackIndex);
            var track = GetTrackByIndex(clip.TrackIndex);
            if (track == null) continue;

            double height = track.Height - 10;
            var clipRect = new Rect(x, y + 5, Math.Max(width, MinClipWidth), height);

            if (clipRect.Contains(point))
            {
                return clip;
            }
        }

        return null;
    }

    /// <summary>
    /// 클립 엣지 HitTest (트림 핸들)
    /// </summary>
    private ClipEdge HitTestEdge(ClipModel clip, Point point)
    {
        double x = TimeToX(clip.StartTimeMs);
        double width = DurationToWidth(clip.DurationMs);
        double y = GetTrackYPosition(clip.TrackIndex);
        var track = GetTrackByIndex(clip.TrackIndex);
        if (track == null) return ClipEdge.None;

        double height = track.Height - 10;
        var clipRect = new Rect(x, y + 5, Math.Max(width, MinClipWidth), height);

        if (!clipRect.Contains(point))
            return ClipEdge.None;

        const double EdgeThreshold = 10;

        if (point.X < clipRect.Left + EdgeThreshold)
            return ClipEdge.Left;

        if (point.X > clipRect.Right - EdgeThreshold)
            return ClipEdge.Right;

        return ClipEdge.None;
    }
}

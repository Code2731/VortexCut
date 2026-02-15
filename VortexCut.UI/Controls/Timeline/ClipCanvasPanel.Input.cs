using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;
using VortexCut.Core.Models;
using VortexCut.Interop.Services;
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
        CancelHoverThumbnail();

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

        // 우클릭: 컨텍스트 메뉴 표시
        if (properties.IsRightButtonPressed)
        {
            ShowClipContextMenu(point);
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
                // 빈 공간 클릭: 선택 해제 + Playhead 이동 + 스크럽 시작
                if (_viewModel != null)
                {
                    _viewModel.SelectedClips.Clear();
                    _viewModel.SelectedClip = null;
                    long clickedTimeMs = XToTime(point.X);
                    _viewModel.CurrentTimeMs = Math.Max(0, clickedTimeMs);
                    _isScrubbing = true;
                    _scrubGridY = point.Y;
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

        // 스크럽 처리 (빈 공간 드래그 → Playhead 이동 + 썸네일 그리드)
        if (_isScrubbing && _viewModel != null)
        {
            long scrubTimeMs = Math.Max(0, XToTime(point.X));
            _viewModel.CurrentTimeMs = scrubTimeMs;

            // 500ms 이상 변화 시 그리드 갱신
            if (!_scrubGridVisible || Math.Abs(_scrubGridLastUpdateMs - scrubTimeMs) > 500)
            {
                RequestScrubGrid(scrubTimeMs);
            }

            InvalidateVisual();
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

                // 트림 중 스냅 (왼쪽 에지)
                if (_snapService != null && _viewModel != null && _viewModel.SnapEnabled)
                {
                    var snapResult = _snapService.GetSnapTarget(_draggingClip.StartTimeMs, _draggingClip);
                    if (snapResult.Snapped)
                    {
                        var snapDelta = snapResult.TimeMs - _draggingClip.StartTimeMs;
                        _draggingClip.StartTimeMs = snapResult.TimeMs;
                        _draggingClip.DurationMs -= snapDelta;
                        if (_draggingClip.DurationMs < 100) _draggingClip.DurationMs = 100;
                    }
                    _lastSnappedTimeMs = snapResult.Snapped ? snapResult.TimeMs : -1;
                }
                else
                {
                    _lastSnappedTimeMs = -1;
                }
            }
            else if (_trimEdge == ClipEdge.Right)
            {
                var newEndTime = Math.Max(_draggingClip.StartTimeMs + 100, currentTime);
                _draggingClip.DurationMs = newEndTime - _draggingClip.StartTimeMs;

                // 트림 중 스냅 (오른쪽 에지)
                if (_snapService != null && _viewModel != null && _viewModel.SnapEnabled)
                {
                    var endTimeMs = _draggingClip.StartTimeMs + _draggingClip.DurationMs;
                    var snapResult = _snapService.GetSnapTarget(endTimeMs, _draggingClip);
                    if (snapResult.Snapped)
                    {
                        _draggingClip.DurationMs = snapResult.TimeMs - _draggingClip.StartTimeMs;
                        if (_draggingClip.DurationMs < 100) _draggingClip.DurationMs = 100;
                    }
                    _lastSnappedTimeMs = snapResult.Snapped ? snapResult.TimeMs : -1;
                }
                else
                {
                    _lastSnappedTimeMs = -1;
                }
            }

            // 트림 프리뷰 요청 (에지 프레임 표시)
            RequestTrimPreview(_draggingClip);

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

            // 트림 에지 호버 감지 (커서 변경 + 핸들 하이라이트)
            if (hoveredClip != null)
            {
                var edge = HitTestEdge(hoveredClip, point);
                if (edge != _hoveredEdge)
                {
                    _hoveredEdge = edge;
                    Cursor = edge != ClipEdge.None
                        ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                        : Avalonia.Input.Cursor.Default;
                    InvalidateVisual();
                }

                // 호버 썸네일 프리뷰 트리거 (에지가 아닌 곳에서만)
                if (edge == ClipEdge.None && !(_viewModel?.IsPlaying ?? false))
                {
                    _hoverThumbnailPos = point;
                    RequestHoverThumbnail(hoveredClip, point);
                }
                else
                {
                    CancelHoverThumbnail();
                }
            }
            else
            {
                CancelHoverThumbnail();
                if (_hoveredEdge != ClipEdge.None)
                {
                    _hoveredEdge = ClipEdge.None;
                    Cursor = Avalonia.Input.Cursor.Default;
                    InvalidateVisual();
                }
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // 스크럽 종료 (썸네일 그리드 해제)
        if (_isScrubbing)
        {
            _isScrubbing = false;
            _scrubGridVisible = false;
            _scrubGridLastUpdateMs = -1;
            InvalidateVisual();
        }

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
            _lastSnappedTimeMs = -1;
            _trimPreviewVisible = false;
            _trimPreviewTimeMs = -1;
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

        // Y 범위 체크 (클립 안에 있는지)
        if (point.Y < clipRect.Top || point.Y > clipRect.Bottom)
            return ClipEdge.None;

        const double EdgeThreshold = 14; // 안쪽 감지 범위
        const double OutsideExtend = 4;  // 바깥쪽 확장 범위

        // 왼쪽 에지: 클립 바깥 4px ~ 안쪽 14px
        if (point.X >= clipRect.Left - OutsideExtend && point.X < clipRect.Left + EdgeThreshold)
            return ClipEdge.Left;

        // 오른쪽 에지: 안쪽 14px ~ 클립 바깥 4px
        if (point.X > clipRect.Right - EdgeThreshold && point.X <= clipRect.Right + OutsideExtend)
            return ClipEdge.Right;

        return ClipEdge.None;
    }

    /// <summary>
    /// 우클릭 컨텍스트 메뉴 표시
    /// </summary>
    private void ShowClipContextMenu(Point point)
    {
        if (_viewModel == null) return;

        // 우클릭한 위치의 클립 선택
        var clickedClip = GetClipAtPosition(point);
        if (clickedClip != null && !_viewModel.SelectedClips.Contains(clickedClip))
        {
            _viewModel.SelectedClips.Clear();
            _viewModel.SelectedClips.Add(clickedClip);
            _viewModel.SelectedClip = clickedClip;
            InvalidateVisual();
        }

        bool hasSelection = _viewModel.SelectedClips.Count > 0;

        var menu = new ContextMenu();

        // Cut
        var cutItem = new MenuItem { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
        cutItem.Click += (_, _) => _viewModel.CutSelectedClips();
        cutItem.IsEnabled = hasSelection;
        menu.Items.Add(cutItem);

        // Copy
        var copyItem = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copyItem.Click += (_, _) => _viewModel.CopySelectedClips();
        copyItem.IsEnabled = hasSelection;
        menu.Items.Add(copyItem);

        // Paste
        var pasteItem = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
        pasteItem.Click += (_, _) => _viewModel.PasteClips();
        menu.Items.Add(pasteItem);

        menu.Items.Add(new Separator());

        // Delete
        var deleteItem = new MenuItem { Header = "Delete", InputGesture = new KeyGesture(Key.Delete) };
        deleteItem.Click += (_, _) => _viewModel.DeleteSelectedClips();
        deleteItem.IsEnabled = hasSelection;
        menu.Items.Add(deleteItem);

        // Duplicate
        var duplicateItem = new MenuItem { Header = "Duplicate", InputGesture = new KeyGesture(Key.D, KeyModifiers.Control) };
        duplicateItem.Click += (_, _) => _viewModel.DuplicateSelectedClips();
        duplicateItem.IsEnabled = hasSelection;
        menu.Items.Add(duplicateItem);

        menu.Items.Add(new Separator());

        // Split at Playhead
        var splitItem = new MenuItem { Header = "Split at Playhead", InputGesture = new KeyGesture(Key.S) };
        splitItem.Click += (_, _) => _viewModel.SplitAtPlayhead();
        menu.Items.Add(splitItem);

        // Select All
        var selectAllItem = new MenuItem { Header = "Select All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAllItem.Click += (_, _) => _viewModel.SelectAllClips();
        menu.Items.Add(selectAllItem);

        this.ContextMenu = menu;
        menu.Open(this);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        CancelHoverThumbnail();
        _hoveredClip = null;
        _hoveredEdge = ClipEdge.None;
        Cursor = Avalonia.Input.Cursor.Default;
        InvalidateVisual();
    }

    /// <summary>
    /// 호버 썸네일 요청 (200ms 디바운스)
    /// </summary>
    private void RequestHoverThumbnail(ClipModel clip, Point mousePos)
    {
        // 마우스 위치의 타임라인 시간 계산
        long timeMs = XToTime(mousePos.X);

        // 클립 내부 시간 → 소스 시간
        long localTimeMs = timeMs - clip.StartTimeMs;
        if (localTimeMs < 0 || localTimeMs > clip.DurationMs) return;
        long sourceTimeMs = clip.TrimStartMs + localTimeMs;

        // 이미 같은 시간이면 스킵
        if (_hoverThumbnailVisible && Math.Abs(_hoverThumbnailTimeMs - sourceTimeMs) < 200)
        {
            _hoverThumbnailPos = mousePos;
            return;
        }

        // 이전 디바운스 취소
        _hoverDebounceTokenSource?.Cancel();
        _hoverDebounceTokenSource = new CancellationTokenSource();
        var token = _hoverDebounceTokenSource.Token;

        _ = DebounceHoverThumbnail(clip.FilePath, sourceTimeMs, mousePos, token);
    }

    private async Task DebounceHoverThumbnail(string filePath, long sourceTimeMs, Point mousePos, CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            if (token.IsCancellationRequested) return;

            // 단일 렌더 슬롯
            if (Interlocked.CompareExchange(ref _hoverThumbnailRenderActive, 1, 0) != 0)
                return;

            byte[]? frameData = null;
            uint width = 0, height = 0;

            await Task.Run(() =>
            {
                try
                {
                    // 세션 재사용 (같은 파일이면)
                    if (_hoverSessionFilePath != filePath || _hoverThumbnailSession == null)
                    {
                        _hoverThumbnailSession?.Dispose();
                        _hoverThumbnailSession = ThumbnailSession.Create(filePath, 160, 90);
                        _hoverSessionFilePath = filePath;
                    }

                    using var frame = _hoverThumbnailSession.Generate(sourceTimeMs);
                    if (frame != null)
                    {
                        frameData = frame.Data.ToArray();
                        width = frame.Width;
                        height = frame.Height;
                    }
                }
                catch { /* 디코딩 실패 무시 */ }
            }, token);

            if (token.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _hoverThumbnailRenderActive, 0);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (frameData != null && width > 0 && height > 0)
                {
                    var pixelSize = new Avalonia.PixelSize((int)width, (int)height);
                    var bmp = new WriteableBitmap(pixelSize, new Avalonia.Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);

                    using (var buffer = bmp.Lock())
                    {
                        unsafe
                        {
                            fixed (byte* src = frameData)
                            {
                                var size = (int)width * (int)height * 4;
                                Buffer.MemoryCopy(src, (byte*)buffer.Address, size, size);
                            }
                        }
                    }

                    _hoverThumbnailBitmap = bmp;
                    _hoverThumbnailTimeMs = sourceTimeMs;
                    _hoverThumbnailPos = mousePos;
                    _hoverThumbnailVisible = true;
                    InvalidateVisual();
                }
            });

            Interlocked.Exchange(ref _hoverThumbnailRenderActive, 0);
        }
        catch (TaskCanceledException) { Interlocked.Exchange(ref _hoverThumbnailRenderActive, 0); }
        catch { Interlocked.Exchange(ref _hoverThumbnailRenderActive, 0); }
    }

    /// <summary>
    /// 호버 썸네일 취소
    /// </summary>
    private void CancelHoverThumbnail()
    {
        _hoverDebounceTokenSource?.Cancel();
        if (_hoverThumbnailVisible)
        {
            _hoverThumbnailVisible = false;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 트림 중 에지 프레임 프리뷰 요청 (30ms 이상 변화 시만 갱신)
    /// </summary>
    private void RequestTrimPreview(ClipModel clip)
    {
        // 에지 프레임의 소스 시간 계산
        long edgeSourceTimeMs = _trimEdge == ClipEdge.Left
            ? clip.TrimStartMs
            : clip.TrimStartMs + clip.DurationMs;

        // 30ms 이상 변화 시만 갱신
        if (_trimPreviewVisible && Math.Abs(_trimPreviewTimeMs - edgeSourceTimeMs) < 30)
            return;

        // 단일 렌더 슬롯
        if (Interlocked.CompareExchange(ref _trimPreviewRenderActive, 1, 0) != 0)
            return;

        _ = GenerateTrimPreview(clip.FilePath, edgeSourceTimeMs);
    }

    private async Task GenerateTrimPreview(string filePath, long sourceTimeMs)
    {
        try
        {
            byte[]? frameData = null;
            uint width = 0, height = 0;

            await Task.Run(() =>
            {
                try
                {
                    // 호버 세션 재사용
                    if (_hoverSessionFilePath != filePath || _hoverThumbnailSession == null)
                    {
                        _hoverThumbnailSession?.Dispose();
                        _hoverThumbnailSession = ThumbnailSession.Create(filePath, 160, 90);
                        _hoverSessionFilePath = filePath;
                    }

                    using var frame = _hoverThumbnailSession.Generate(sourceTimeMs);
                    if (frame != null)
                    {
                        frameData = frame.Data.ToArray();
                        width = frame.Width;
                        height = frame.Height;
                    }
                }
                catch { /* 디코딩 실패 무시 */ }
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (frameData != null && width > 0 && height > 0)
                {
                    var pixelSize = new Avalonia.PixelSize((int)width, (int)height);
                    var bmp = new WriteableBitmap(pixelSize, new Avalonia.Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);

                    using (var buffer = bmp.Lock())
                    {
                        unsafe
                        {
                            fixed (byte* src = frameData)
                            {
                                var size = (int)width * (int)height * 4;
                                Buffer.MemoryCopy(src, (byte*)buffer.Address, size, size);
                            }
                        }
                    }

                    _trimPreviewBitmap = bmp;
                    _trimPreviewTimeMs = sourceTimeMs;
                    _trimPreviewVisible = true;
                    InvalidateVisual();
                }
            });
        }
        catch { /* 무시 */ }
        finally
        {
            Interlocked.Exchange(ref _trimPreviewRenderActive, 0);
        }
    }

    /// <summary>
    /// Swifter 스크럽 썸네일 그리드 요청 (4x2 = 8 프레임)
    /// </summary>
    private void RequestScrubGrid(long centerTimeMs)
    {
        if (Interlocked.CompareExchange(ref _scrubGridRenderActive, 1, 0) != 0)
            return;

        // 플레이헤드 아래 클립 찾기 (프레임 소스)
        ClipModel? sourceClip = null;
        foreach (var clip in _clips)
        {
            if (clip.TrackIndex < (_viewModel?.VideoTracks.Count ?? 0) &&
                clip.StartTimeMs <= centerTimeMs && clip.EndTimeMs > centerTimeMs)
            {
                sourceClip = clip;
                break;
            }
        }

        if (sourceClip == null)
        {
            Interlocked.Exchange(ref _scrubGridRenderActive, 0);
            return;
        }

        // 간격: 뷰포트 기준 자동 계산 (전체 뷰 시간 / 16)
        long viewDurationMs = XToTime(Bounds.Width) - XToTime(0);
        long intervalMs = Math.Max(100, viewDurationMs / 16);

        _ = GenerateScrubGrid(sourceClip.FilePath, sourceClip, centerTimeMs, intervalMs);
    }

    private async Task GenerateScrubGrid(string filePath, ClipModel sourceClip, long centerTimeMs, long intervalMs)
    {
        try
        {
            // 8개 타임스탬프 계산 (현재 ±3 간격)
            var timestamps = new long[8];
            for (int i = 0; i < 8; i++)
            {
                long timeMs = centerTimeMs + (i - 3) * intervalMs;
                // 클립 내 소스 시간 → TrimStartMs + 로컬 오프셋
                long localMs = timeMs - sourceClip.StartTimeMs;
                localMs = Math.Clamp(localMs, 0, sourceClip.DurationMs);
                timestamps[i] = sourceClip.TrimStartMs + localMs;
            }

            var bitmaps = new WriteableBitmap?[8];

            await Task.Run(() =>
            {
                try
                {
                    if (_hoverSessionFilePath != filePath || _hoverThumbnailSession == null)
                    {
                        _hoverThumbnailSession?.Dispose();
                        _hoverThumbnailSession = ThumbnailSession.Create(filePath, 160, 90);
                        _hoverSessionFilePath = filePath;
                    }

                    for (int i = 0; i < 8; i++)
                    {
                        try
                        {
                            using var frame = _hoverThumbnailSession.Generate(timestamps[i]);
                            if (frame != null)
                            {
                                var data = frame.Data.ToArray();
                                var w = frame.Width;
                                var h = frame.Height;
                                var pixelSize = new Avalonia.PixelSize((int)w, (int)h);
                                var bmp = new WriteableBitmap(pixelSize, new Avalonia.Vector(96, 96),
                                    Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
                                using (var buffer = bmp.Lock())
                                {
                                    unsafe
                                    {
                                        fixed (byte* src = data)
                                        {
                                            var size = (int)w * (int)h * 4;
                                            Buffer.MemoryCopy(src, (byte*)buffer.Address, size, size);
                                        }
                                    }
                                }
                                bitmaps[i] = bmp;
                            }
                        }
                        catch { /* 개별 프레임 실패 무시 */ }
                    }
                }
                catch { /* 세션 실패 무시 */ }
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _scrubGridBitmaps = bitmaps;
                for (int i = 0; i < 8; i++)
                    _scrubGridTimeMs[i] = centerTimeMs + (i - 3) * intervalMs;
                _scrubGridLastUpdateMs = centerTimeMs;
                _scrubGridVisible = true;
                InvalidateVisual();
            });
        }
        catch { /* 무시 */ }
        finally
        {
            Interlocked.Exchange(ref _scrubGridRenderActive, 0);
        }
    }
}

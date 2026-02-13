using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;
using VortexCut.UI.Services;

namespace VortexCut.UI.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private readonly ToastService _toastService = new();

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // 키보드 단축키
        KeyDown += OnKeyDown;

        // StorageProvider 설정 및 초기화
        Opened += (sender, e) =>
        {
            _viewModel.SetStorageProvider(StorageProvider);

            // Toast 서비스 초기화
            var toastContainer = this.FindControl<Grid>("ToastContainer");
            if (toastContainer != null)
            {
                System.Diagnostics.Debug.WriteLine("✅ ToastContainer found, initializing ToastService...");
                _toastService.Initialize(toastContainer);
                _viewModel.SetToastService(_toastService);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("❌ ToastContainer NOT found!");
            }

            // Export 다이얼로그 열기 콜백
            _viewModel.RequestOpenExportDialog = OpenExportDialog;

            _viewModel.Initialize(); // 첫 프로젝트 생성
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel == null) return;

        // Modifier keys
        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // Function keys (After Effects style)
        if (e.Key == Key.F9)
        {
            // F9: Easy Ease (EaseInOut)
            ApplyInterpolationToSelectedKeyframes(InterpolationType.EaseInOut);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F9 && isShift && !isCtrl)
        {
            // Shift+F9: Easy Ease In
            ApplyInterpolationToSelectedKeyframes(InterpolationType.EaseIn);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F9 && isShift && isCtrl)
        {
            // Ctrl+Shift+F9: Easy Ease Out
            ApplyInterpolationToSelectedKeyframes(InterpolationType.EaseOut);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            // 키프레임 & 마커
            case Key.K:
                if (!isCtrl && !isShift)
                {
                    // K: 키프레임 추가 (현재 Playhead 위치)
                    _viewModel.Timeline.AddKeyframeAtCurrentTime();
                    e.Handled = true;
                }
                break;

            case Key.M:
                if (!isCtrl && !isShift)
                {
                    // M: 마커 추가 (현재 Playhead 위치)
                    _viewModel.Timeline.AddMarker(
                        _viewModel.Timeline.CurrentTimeMs,
                        $"Marker {_viewModel.Timeline.Markers.Count + 1}");
                    e.Handled = true;
                }
                break;

            // 네비게이션 (After Effects style)
            case Key.J:
                // J: 이전 키프레임으로 이동
                JumpToPreviousKeyframe();
                e.Handled = true;
                break;

            case Key.L:
                // L: 다음 키프레임으로 이동
                JumpToNextKeyframe();
                e.Handled = true;
                break;

            case Key.Home:
                // Home: 타임라인 시작으로
                _viewModel.Timeline.CurrentTimeMs = 0;
                e.Handled = true;
                break;

            case Key.End:
                // End: 타임라인 끝으로
                var maxTime = _viewModel.Timeline.Clips
                    .Select(c => c.EndTimeMs)
                    .DefaultIfEmpty(0)
                    .Max();
                _viewModel.Timeline.CurrentTimeMs = maxTime;
                e.Handled = true;
                break;

            // 편집
            case Key.Delete:
            case Key.Back:
                // Delete/Backspace: 선택된 클립 삭제
                DeleteSelectedClips();
                e.Handled = true;
                break;

            case Key.D:
                if (isCtrl)
                {
                    // Ctrl+D: 선택된 클립 복제
                    DuplicateSelectedClips();
                    e.Handled = true;
                }
                break;

            // 재생
            case Key.Space:
                // Space: 재생/일시정지
                _viewModel.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;

            // In/Out 포인트 & Import
            case Key.I:
                if (isCtrl)
                {
                    // Ctrl+I: 미디어 임포트
                    _ = _viewModel.OpenVideoFileCommand.ExecuteAsync(null);
                    e.Handled = true;
                }
                else
                {
                    // I: In 포인트 설정
                    _viewModel.Timeline.SetInPoint(_viewModel.Timeline.CurrentTimeMs);
                    e.Handled = true;
                }
                break;

            case Key.O:
                // O: Out 포인트 설정
                _viewModel.Timeline.SetOutPoint(_viewModel.Timeline.CurrentTimeMs);
                e.Handled = true;
                break;

            // Snap 토글
            case Key.S:
                if (!isCtrl && !isShift)
                {
                    // S: Snap 토글
                    _viewModel.Timeline.SnapEnabled = !_viewModel.Timeline.SnapEnabled;
                    e.Handled = true;
                }
                break;

            // Razor 모드 토글
            case Key.C:
                if (!isCtrl && !isShift)
                {
                    // C: Razor 모드 토글 (Cut)
                    _viewModel.Timeline.RazorModeEnabled = !_viewModel.Timeline.RazorModeEnabled;
                    e.Handled = true;
                }
                break;

            // 선택
            case Key.A:
                if (isCtrl)
                {
                    // Ctrl+A: 모든 클립 선택
                    SelectAllClips();
                    e.Handled = true;
                }
                break;

            // 전역 Display Mode 순환
            case Key.T:
                if (isCtrl && isShift)
                {
                    // Ctrl+Shift+T: 전역 표시 모드 순환
                    _viewModel.Timeline.CycleGlobalDisplayMode();
                    e.Handled = true;
                }
                break;
        }
    }

    private void ApplyInterpolationToSelectedKeyframes(InterpolationType interpolation)
    {
        if (_viewModel == null) return;

        // 선택된 클립의 키프레임에 보간 타입 적용
        foreach (var clip in _viewModel.Timeline.SelectedClips)
        {
            var keyframeSystem = GetCurrentKeyframeSystem(clip);
            if (keyframeSystem != null)
            {
                foreach (var keyframe in keyframeSystem.Keyframes)
                {
                    keyframe.Interpolation = interpolation;
                }
            }
        }
    }

    private void JumpToPreviousKeyframe()
    {
        if (_viewModel == null) return;

        var currentTime = _viewModel.Timeline.CurrentTimeMs;
        long? previousTime = null;

        // 모든 키프레임과 마커 중 이전 위치 찾기
        foreach (var clip in _viewModel.Timeline.SelectedClips)
        {
            var keyframeSystem = GetCurrentKeyframeSystem(clip);
            if (keyframeSystem != null)
            {
                foreach (var kf in keyframeSystem.Keyframes)
                {
                    var kfTime = clip.StartTimeMs + (long)(kf.Time * 1000);
                    if (kfTime < currentTime)
                    {
                        if (!previousTime.HasValue || kfTime > previousTime.Value)
                        {
                            previousTime = kfTime;
                        }
                    }
                }
            }
        }

        // 마커도 확인
        foreach (var marker in _viewModel.Timeline.Markers)
        {
            if (marker.TimeMs < currentTime)
            {
                if (!previousTime.HasValue || marker.TimeMs > previousTime.Value)
                {
                    previousTime = marker.TimeMs;
                }
            }
        }

        if (previousTime.HasValue)
        {
            _viewModel.Timeline.CurrentTimeMs = previousTime.Value;
        }
    }

    private void JumpToNextKeyframe()
    {
        if (_viewModel == null) return;

        var currentTime = _viewModel.Timeline.CurrentTimeMs;
        long? nextTime = null;

        // 모든 키프레임과 마커 중 다음 위치 찾기
        foreach (var clip in _viewModel.Timeline.SelectedClips)
        {
            var keyframeSystem = GetCurrentKeyframeSystem(clip);
            if (keyframeSystem != null)
            {
                foreach (var kf in keyframeSystem.Keyframes)
                {
                    var kfTime = clip.StartTimeMs + (long)(kf.Time * 1000);
                    if (kfTime > currentTime)
                    {
                        if (!nextTime.HasValue || kfTime < nextTime.Value)
                        {
                            nextTime = kfTime;
                        }
                    }
                }
            }
        }

        // 마커도 확인
        foreach (var marker in _viewModel.Timeline.Markers)
        {
            if (marker.TimeMs > currentTime)
            {
                if (!nextTime.HasValue || marker.TimeMs < nextTime.Value)
                {
                    nextTime = marker.TimeMs;
                }
            }
        }

        if (nextTime.HasValue)
        {
            _viewModel.Timeline.CurrentTimeMs = nextTime.Value;
        }
    }

    private void DeleteSelectedClips()
    {
        if (_viewModel == null) return;

        var clipsToDelete = _viewModel.Timeline.SelectedClips.ToList();
        foreach (var clip in clipsToDelete)
        {
            _viewModel.Timeline.Clips.Remove(clip);
        }
        _viewModel.Timeline.SelectedClips.Clear();
    }

    private void DuplicateSelectedClips()
    {
        if (_viewModel == null) return;

        var clipsToDuplicate = _viewModel.Timeline.SelectedClips.ToList();
        _viewModel.Timeline.SelectedClips.Clear();

        foreach (var clip in clipsToDuplicate)
        {
            var newClip = new Core.Models.ClipModel
            {
                Id = (ulong)Random.Shared.NextInt64(),
                FilePath = clip.FilePath,
                StartTimeMs = clip.EndTimeMs, // 원본 바로 뒤에 배치
                DurationMs = clip.DurationMs,
                TrackIndex = clip.TrackIndex
            };
            _viewModel.Timeline.Clips.Add(newClip);
            _viewModel.Timeline.SelectedClips.Add(newClip);
        }
    }

    private void SelectAllClips()
    {
        if (_viewModel == null) return;

        _viewModel.Timeline.SelectedClips.Clear();
        foreach (var clip in _viewModel.Timeline.Clips)
        {
            _viewModel.Timeline.SelectedClips.Add(clip);
        }
    }

    /// <summary>
    /// Export 다이얼로그 열기
    /// </summary>
    private async void OpenExportDialog()
    {
        if (_viewModel == null) return;

        var exportVm = new ExportViewModel(_viewModel.ProjectService);

        var dialog = new ExportDialog
        {
            DataContext = exportVm
        };

        // Export 완료 시 다이얼로그 닫기
        exportVm.OnExportComplete = () =>
        {
            _toastService.ShowSuccess("Export 완료", "영상이 성공적으로 저장되었습니다.");
        };

        await dialog.ShowDialog(this);

        // 다이얼로그 닫힌 후 리소스 정리
        exportVm.Dispose();
    }

    /// <summary>
    private KeyframeSystem? GetCurrentKeyframeSystem(ClipModel clip)
    {
        if (_viewModel == null) return null;

        return _viewModel.Timeline.SelectedKeyframeSystem switch
        {
            KeyframeSystemType.Opacity => clip.OpacityKeyframes,
            KeyframeSystemType.Volume => clip.VolumeKeyframes,
            KeyframeSystemType.PositionX => clip.PositionXKeyframes,
            KeyframeSystemType.PositionY => clip.PositionYKeyframes,
            KeyframeSystemType.Scale => clip.ScaleKeyframes,
            KeyframeSystemType.Rotation => clip.RotationKeyframes,
            _ => null
        };
    }
}

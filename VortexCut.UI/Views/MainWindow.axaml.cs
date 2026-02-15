using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;
using VortexCut.UI.Services;

namespace VortexCut.UI.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private readonly ToastService _toastService = new();

    // 워크스페이스 버튼
    private Button? _wsEditingBtn;
    private Button? _wsColorBtn;
    private Button? _wsAudioBtn;
    private Button? _wsEffectsBtn;

    /// <summary>
    /// XAML 런타임 로더용 (디자이너/프리뷰어에서 사용)
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        _viewModel = viewModel;
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

            // 워크스페이스 버튼 초기화
            SetupWorkspaceButtons();

            _viewModel.Initialize(); // 첫 프로젝트 생성
        };
    }

    private void SetupWorkspaceButtons()
    {
        _wsEditingBtn = this.FindControl<Button>("WorkspaceEditingBtn");
        _wsColorBtn = this.FindControl<Button>("WorkspaceColorBtn");
        _wsAudioBtn = this.FindControl<Button>("WorkspaceAudioBtn");
        _wsEffectsBtn = this.FindControl<Button>("WorkspaceEffectsBtn");

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "ActiveWorkspace")
                    UpdateWorkspaceButtonStyles();
            };
        }
    }

    private void UpdateWorkspaceButtonStyles()
    {
        if (_viewModel == null) return;

        var accentBrush = this.FindResource("AccentBrush") as IBrush ?? Brushes.DodgerBlue;
        var brightTextBrush = this.FindResource("TextBrightBrush") as IBrush ?? Brushes.White;
        var active = _viewModel.ActiveWorkspace;

        Button?[] buttons = { _wsEditingBtn, _wsColorBtn, _wsAudioBtn, _wsEffectsBtn };
        string[] names = { "Editing", "Color", "Audio", "Effects" };

        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] == null) continue;

            if (names[i] == active)
            {
                buttons[i]!.Background = accentBrush;
                buttons[i]!.Foreground = brightTextBrush;
                buttons[i]!.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                buttons[i]!.Background = Brushes.Transparent;
                buttons[i]!.ClearValue(Button.ForegroundProperty);
                buttons[i]!.FontWeight = FontWeight.Normal;
            }
        }
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
            if (isCtrl && isShift)
                _viewModel.Timeline.ApplyKeyframeInterpolationCommand.Execute(InterpolationType.EaseOut);
            else if (isShift)
                _viewModel.Timeline.ApplyKeyframeInterpolationCommand.Execute(InterpolationType.EaseIn);
            else
                _viewModel.Timeline.ApplyKeyframeInterpolationCommand.Execute(InterpolationType.EaseInOut);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            // Undo/Redo
            case Key.Z:
                if (isCtrl && isShift)
                {
                    // Ctrl+Shift+Z: Redo
                    _viewModel.Timeline.Redo();
                    e.Handled = true;
                }
                else if (isCtrl)
                {
                    // Ctrl+Z: Undo
                    _viewModel.Timeline.Undo();
                    e.Handled = true;
                }
                break;

            case Key.Y:
                if (isCtrl)
                {
                    // Ctrl+Y: Redo (대체 키)
                    _viewModel.Timeline.Redo();
                    e.Handled = true;
                }
                break;

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
                _viewModel.Timeline.JumpToPreviousKeyframeCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.L:
                _viewModel.Timeline.JumpToNextKeyframeCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Home:
                _viewModel.Timeline.CurrentTimeMs = 0;
                e.Handled = true;
                break;

            case Key.End:
                _viewModel.Timeline.JumpToEndCommand.Execute(null);
                e.Handled = true;
                break;

            // 편집
            case Key.Delete:
            case Key.Back:
                _viewModel.Timeline.DeleteSelectedClipsCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.D:
                if (isCtrl)
                {
                    _viewModel.Timeline.DuplicateSelectedClipsCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.X:
                if (isCtrl)
                {
                    _viewModel.Timeline.CutSelectedClipsCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.V:
                if (isCtrl)
                {
                    _viewModel.Timeline.PasteClipsCommand.Execute(null);
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
                if (isCtrl && isShift)
                {
                    // Ctrl+Shift+I: SRT 자막 임포트
                    _ = _viewModel.ImportSrtFileCommand.ExecuteAsync(null);
                    e.Handled = true;
                }
                else if (isCtrl)
                {
                    // Ctrl+I: 미디어 임포트
                    _ = _viewModel.OpenVideoFileCommand.ExecuteAsync(null);
                    e.Handled = true;
                }
                else if (_viewModel.Timeline.IsPlaying)
                {
                    // 재생 중 I: 다이나믹 트림 (왼쪽 에지)
                    _viewModel.Timeline.DynamicTrimIn();
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
                if (_viewModel.Timeline.IsPlaying)
                {
                    // 재생 중 O: 다이나믹 트림 (오른쪽 에지)
                    _viewModel.Timeline.DynamicTrimOut();
                }
                else
                {
                    // O: Out 포인트 설정
                    _viewModel.Timeline.SetOutPoint(_viewModel.Timeline.CurrentTimeMs);
                }
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

            // Razor 모드 토글 / Copy
            case Key.C:
                if (isCtrl)
                {
                    // Ctrl+C: 클립 복사
                    _viewModel.Timeline.CopySelectedClipsCommand.Execute(null);
                    e.Handled = true;
                }
                else if (!isShift)
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
                    _viewModel.Timeline.SelectAllClipsCommand.Execute(null);
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

            // 트랙 Arm (1-6: V1-V6, Alt+1-6: A1-A6)
            case Key.D1: case Key.D2: case Key.D3:
            case Key.D4: case Key.D5: case Key.D6:
                if (!isCtrl && !isShift)
                {
                    int trackNum = e.Key - Key.D1; // 0-based
                    if (isAlt)
                        ArmTrack(_viewModel.Timeline.AudioTracks, trackNum);
                    else
                        ArmTrack(_viewModel.Timeline.VideoTracks, trackNum);
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>
    /// 트랙 Arm 토글 (상호배타: 같은 타입 중 1개만)
    /// </summary>
    private void ArmTrack(System.Collections.ObjectModel.ObservableCollection<VortexCut.Core.Models.TrackModel> tracks, int index)
    {
        if (index >= tracks.Count) return;

        bool wasArmed = tracks[index].IsArmed;

        // 모든 동일 타입 트랙 해제
        foreach (var t in tracks)
            t.IsArmed = false;

        // 토글: 이미 armed였으면 해제만, 아니었으면 arm
        if (!wasArmed)
            tracks[index].IsArmed = true;
    }

    /// <summary>
    /// Export 다이얼로그 열기
    /// </summary>
    private async void OpenExportDialog()
    {
        if (_viewModel == null) return;

        var exportVm = new ExportViewModel(_viewModel.ProjectService);
        exportVm.SetTimelineViewModel(_viewModel.Timeline);

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

}

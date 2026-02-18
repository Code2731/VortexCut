using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Views;

/// <summary>
/// Whisper 자동 자막 다이얼로그 code-behind
/// </summary>
public partial class WhisperDialogView : Window
{
    public WhisperDialogView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // ViewModel의 찾아보기 요청 콜백 연결
        if (DataContext is WhisperDialogViewModel vm)
        {
            vm.RequestBrowseModel = OnBrowseModel;
        }
    }

    /// <summary>
    /// "찾아보기..." 선택 시 파일 다이얼로그
    /// </summary>
    private async void OnBrowseModel()
    {
        var fileTypes = new[]
        {
            new FilePickerFileType("Whisper GGML 모델")
            {
                Patterns = new[] { "*.bin" }
            },
            new FilePickerFileType("모든 파일")
            {
                Patterns = new[] { "*.*" }
            }
        };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Whisper 모델 파일 선택",
            FileTypeFilter = fileTypes,
            AllowMultiple = false
        });

        if (result.Count > 0 && DataContext is WhisperDialogViewModel vm)
        {
            vm.SetCustomModelPath(result[0].Path.LocalPath);
        }
    }

    /// <summary>
    /// 닫기 버튼
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

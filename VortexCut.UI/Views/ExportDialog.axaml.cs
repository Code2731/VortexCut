using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Views;

/// <summary>
/// Export 다이얼로그 code-behind
/// </summary>
public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 찾아보기 버튼 클릭 → 파일 저장 다이얼로그
    /// </summary>
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var fileTypes = new[]
        {
            new FilePickerFileType("MP4 Video")
            {
                Patterns = new[] { "*.mp4" }
            }
        };

        var result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export 파일 저장",
            DefaultExtension = "mp4",
            FileTypeChoices = fileTypes,
            SuggestedFileName = "export.mp4"
        });

        if (result != null && DataContext is ExportViewModel vm)
        {
            vm.OutputPath = result.Path.LocalPath;
        }
    }

    /// <summary>
    /// 닫기 버튼 클릭
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace VortexCut.UI.Controls;

/// <summary>
/// 패널 헤더 재사용 컨트롤 (Border + PanelTitle 패턴)
/// </summary>
public partial class PanelHeaderControl : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PanelHeaderControl, string>(nameof(Title), "");

    public static readonly StyledProperty<object?> AdditionalContentProperty =
        AvaloniaProperty.Register<PanelHeaderControl, object?>(nameof(AdditionalContent));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? AdditionalContent
    {
        get => GetValue(AdditionalContentProperty);
        set => SetValue(AdditionalContentProperty, value);
    }
}

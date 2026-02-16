using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VortexCut.UI.Controls;

/// <summary>
/// 통계 표시 컨트롤 (Label + Value 패턴)
/// </summary>
public partial class StatDisplayControl : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StatDisplayControl, string>(nameof(Label), "");

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<StatDisplayControl, string>(nameof(Value), "0");

    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<StatDisplayControl, IBrush?>(nameof(ValueBrush));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }
}

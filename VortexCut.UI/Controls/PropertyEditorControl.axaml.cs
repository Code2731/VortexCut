using Avalonia;
using Avalonia.Controls;

namespace VortexCut.UI.Controls;

/// <summary>
/// 속성 편집 컨트롤 (Label + EditableValue + Slider 패턴)
/// </summary>
public partial class PropertyEditorControl : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<PropertyEditorControl, string>(nameof(Label), "");

    public static readonly StyledProperty<string> DisplayValueProperty =
        AvaloniaProperty.Register<PropertyEditorControl, string>(nameof(DisplayValue), "0");

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<PropertyEditorControl, double>(nameof(Minimum), 0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<PropertyEditorControl, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PropertyEditorControl, double>(nameof(Value), 0);

    public static readonly StyledProperty<double> SmallChangeProperty =
        AvaloniaProperty.Register<PropertyEditorControl, double>(nameof(SmallChange), 1);

    public static readonly StyledProperty<double> LargeChangeProperty =
        AvaloniaProperty.Register<PropertyEditorControl, double>(nameof(LargeChange), 10);

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string DisplayValue
    {
        get => GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double SmallChange
    {
        get => GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    public double LargeChange
    {
        get => GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    // 내부 컨트롤 접근을 위한 속성 (code-behind 호환성)
    public Slider? Slider => this.FindControl<Slider>("PropertySlider");
    public EditableValueText? ValueTextControl => this.FindControl<EditableValueText>("ValueText");
}

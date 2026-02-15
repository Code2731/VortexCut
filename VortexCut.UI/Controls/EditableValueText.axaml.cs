using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VortexCut.UI.Controls;

/// <summary>
/// 편집 가능한 값 텍스트 컨트롤
/// 기본: TextBlock 표시 → 더블클릭: TextBox 편집 → Enter/탈포커스: 확정, Escape: 취소
/// </summary>
public partial class EditableValueText : UserControl
{
    /// <summary>
    /// 표시할 값 텍스트
    /// </summary>
    public static readonly StyledProperty<string> DisplayValueProperty =
        AvaloniaProperty.Register<EditableValueText, string>(nameof(DisplayValue), "0");

    public string DisplayValue
    {
        get => GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    /// <summary>
    /// 편집 모드 여부
    /// </summary>
    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<EditableValueText, bool>(nameof(IsEditing), false);

    public bool IsEditing
    {
        get => GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    /// <summary>
    /// 값 변경 이벤트 (확정된 텍스트 전달)
    /// </summary>
    public event EventHandler<string>? ValueCommitted;

    private TextBlock? _displayText;
    private TextBox? _editBox;

    public EditableValueText()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsEditingProperty)
        {
            UpdateVisibility(IsEditing);
        }
        else if (change.Property == DisplayValueProperty)
        {
            if (_displayText != null) _displayText.Text = DisplayValue;
        }
    }

    private void UpdateVisibility(bool editing)
    {
        if (_displayText != null) _displayText.IsVisible = !editing;
        if (_editBox != null) _editBox.IsVisible = editing;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SetupControls();
    }

    private void SetupControls()
    {
        _displayText = this.FindControl<TextBlock>("DisplayText");
        _editBox = this.FindControl<TextBox>("EditBox");

        // 초기 텍스트 설정
        if (_displayText != null)
        {
            _displayText.Text = DisplayValue;
            _displayText.DoubleTapped -= OnDoubleTapped;
            _displayText.DoubleTapped += OnDoubleTapped;
        }

        if (_editBox != null)
        {
            _editBox.KeyDown -= OnEditBoxKeyDown;
            _editBox.KeyDown += OnEditBoxKeyDown;
            _editBox.LostFocus -= OnEditBoxLostFocus;
            _editBox.LostFocus += OnEditBoxLostFocus;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        StartEditing();
    }

    private void StartEditing()
    {
        if (_editBox == null) return;

        _editBox.Text = DisplayValue;
        IsEditing = true;

        // 포커스 + 전체 선택
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _editBox.Focus();
            _editBox.SelectAll();
        });
    }

    private void CommitEdit()
    {
        if (_editBox == null || !IsEditing) return;

        var newValue = _editBox.Text ?? "";
        IsEditing = false;
        ValueCommitted?.Invoke(this, newValue);
    }

    private void CancelEdit()
    {
        IsEditing = false;
    }

    private void OnEditBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void OnEditBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (IsEditing)
        {
            CommitEdit();
        }
    }
}

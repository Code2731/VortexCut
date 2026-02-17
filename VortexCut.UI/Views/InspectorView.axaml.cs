using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using VortexCut.Core.Models;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Views;

public partial class InspectorView : UserControl
{
    // 워크스페이스 패널
    private Panel? _propertiesPanel;
    private Panel? _colorPanel;
    private Panel? _audioPanel;
    private Panel? _effectsPanel;
    private Panel? _historyPanel;
    private TextBlock? _workspaceTitle;

    // 히스토리 탭 버튼
    private Button? _propertiesTabBtn;
    private Button? _historyTabBtn;
    private bool _isHistoryMode;

    // Color 편집 컨트롤
    private Controls.PropertyEditorControl? _brightnessEditor;
    private Controls.PropertyEditorControl? _contrastEditor;
    private Controls.PropertyEditorControl? _saturationEditor;
    private Controls.PropertyEditorControl? _temperatureEditor;

    // Audio 편집 컨트롤
    private Controls.PropertyEditorControl? _volumeEditor;
    private Controls.PropertyEditorControl? _speedEditor;
    private Controls.PropertyEditorControl? _fadeInEditor;
    private Controls.PropertyEditorControl? _fadeOutEditor;

    // Transition
    private ComboBox? _transitionTypeComboBox;

    // Properties (Editing)
    private TextBlock? _clipNameText;
    private TextBlock? _clipPathText;
    private Controls.PropertyEditorControl? _startTimeEditor;
    private Controls.PropertyEditorControl? _durationEditor;
    private Controls.PropertyEditorControl? _opacityEditor;
    private TextBlock? _trackIndexValueText;

    // Subtitle
    private TextBox? _subtitleTextBox;
    private Slider? _fontSizeSlider;
    private TextBlock? _fontSizeValueText;
    private ComboBox? _subtitlePositionComboBox;
    private ToggleButton? _boldToggle;
    private ToggleButton? _italicToggle;
    private Panel? _subtitleEditPanel;

    // 프로그래밍적 값 설정 시 이벤트 무시용 플래그
    private bool _isUpdatingSliders;

    public InspectorView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SetupWorkspacePanels();
        SetupPropertiesControls();
        SetupColorSliders();
        SetupAudioSliders();
        SetupTransitionControls();
        SetupSubtitleControls();
    }

    private InspectorViewModel? GetInspectorVm() => (DataContext as MainViewModel)?.Inspector;
    private ClipModel? GetSelectedClip() => (DataContext as MainViewModel)?.Timeline.SelectedClip;

    // ==================== 워크스페이스 전환 ====================

    private void SetupWorkspacePanels()
    {
        _propertiesPanel = this.FindControl<Panel>("PropertiesPanel");
        _colorPanel = this.FindControl<Panel>("ColorPanel");
        _audioPanel = this.FindControl<Panel>("AudioPanel");
        _effectsPanel = this.FindControl<Panel>("EffectsPanel");
        _historyPanel = this.FindControl<Panel>("HistoryPanel");
        _workspaceTitle = this.FindControl<TextBlock>("WorkspaceTitle");
        _propertiesTabBtn = this.FindControl<Button>("PropertiesTabBtn");
        _historyTabBtn = this.FindControl<Button>("HistoryTabBtn");

        if (DataContext is MainViewModel mainVm)
        {
            // 워크스페이스 변경 감지
            mainVm.PropertyChanged += OnMainViewModelPropertyChanged;
            // 클립 선택 변경 감지
            mainVm.Timeline.PropertyChanged += OnTimelinePropertyChanged;
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "ActiveWorkspace")
        {
            UpdateWorkspacePanel();
        }
    }

    private void UpdateWorkspacePanel()
    {
        var mainVm = DataContext as MainViewModel;
        if (mainVm == null) return;

        // 히스토리 모드이면 히스토리 패널만 표시
        if (_isHistoryMode)
        {
            if (_propertiesPanel != null) _propertiesPanel.IsVisible = false;
            if (_colorPanel != null) _colorPanel.IsVisible = false;
            if (_audioPanel != null) _audioPanel.IsVisible = false;
            if (_effectsPanel != null) _effectsPanel.IsVisible = false;
            if (_historyPanel != null) _historyPanel.IsVisible = true;
            if (_workspaceTitle != null) _workspaceTitle.Text = "HISTORY";
            return;
        }

        var workspace = mainVm.ActiveWorkspace;

        if (_propertiesPanel != null) _propertiesPanel.IsVisible = workspace == "Editing";
        if (_colorPanel != null) _colorPanel.IsVisible = workspace == "Color";
        if (_audioPanel != null) _audioPanel.IsVisible = workspace == "Audio";
        if (_effectsPanel != null) _effectsPanel.IsVisible = workspace == "Effects";
        if (_historyPanel != null) _historyPanel.IsVisible = false;

        // 워크스페이스 전환 시 활성 패널 슬라이더 재동기화
        switch (workspace)
        {
            case "Editing": SyncPropertiesToClip(); break;
            case "Color": SyncSlidersToClip(); break;
            case "Audio": SyncAudioSlidersToClip(); break;
            case "Effects": SyncTransitionToClip(); SyncSubtitleToClip(); break;
        }

        if (_workspaceTitle != null)
        {
            _workspaceTitle.Text = workspace switch
            {
                "Editing" => "PROPERTIES",
                "Color" => "COLOR",
                "Audio" => "AUDIO",
                "Effects" => "EFFECTS",
                _ => workspace.ToUpper()
            };
        }
    }

    // ==================== History 탭 ====================

    private void OnPropertiesTabClick(object? sender, RoutedEventArgs e)
    {
        _isHistoryMode = false;
        SetTabActiveState(false);
        UpdateWorkspacePanel();
    }

    private void OnHistoryTabClick(object? sender, RoutedEventArgs e)
    {
        _isHistoryMode = true;
        SetTabActiveState(true);
        UpdateWorkspacePanel();
    }

    private void SetTabActiveState(bool historyActive)
    {
        if (_propertiesTabBtn != null)
        {
            _propertiesTabBtn.Classes.Remove("HeaderTabActive");
            _propertiesTabBtn.Classes.Remove("HeaderTab");
            _propertiesTabBtn.Classes.Add(historyActive ? "HeaderTab" : "HeaderTabActive");
        }
        if (_historyTabBtn != null)
        {
            _historyTabBtn.Classes.Remove("HeaderTabActive");
            _historyTabBtn.Classes.Remove("HeaderTab");
            _historyTabBtn.Classes.Add(historyActive ? "HeaderTabActive" : "HeaderTab");
        }
    }

    private void OnHistoryItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not Services.HistoryEntry entry) return;
        (DataContext as MainViewModel)?.Timeline.NavigateHistoryCommand.Execute(entry);
    }

    private void OnClearHistoryClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.Timeline.ClearHistoryCommand.Execute(null);
    }

    private void OnTimelinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "SelectedClip")
        {
            SyncPropertiesToClip();
            SyncSlidersToClip();
            SyncAudioSlidersToClip();
            SyncTransitionToClip();
            SyncSubtitleToClip();

            // 자막 클립 선택 시 → Effects 워크스페이스로 자동 전환
            var clip = GetSelectedClip();
            if (clip is SubtitleClipModel && DataContext is MainViewModel mainVm)
            {
                mainVm.ActiveWorkspace = "Effects";
            }
        }
    }

    // ==================== Properties (Editing) ====================

    private void SetupPropertiesControls()
    {
        _clipNameText = this.FindControl<TextBlock>("ClipNameText");
        _clipPathText = this.FindControl<TextBlock>("ClipPathText");
        _startTimeEditor = this.FindControl<Controls.PropertyEditorControl>("StartTimeEditor");
        _durationEditor = this.FindControl<Controls.PropertyEditorControl>("DurationEditor");
        _opacityEditor = this.FindControl<Controls.PropertyEditorControl>("OpacityEditor");
        _trackIndexValueText = this.FindControl<TextBlock>("TrackIndexValueText");

        if (_startTimeEditor?.Slider != null) _startTimeEditor.Slider.ValueChanged += OnPropertiesSliderChanged;
        if (_durationEditor?.Slider != null) _durationEditor.Slider.ValueChanged += OnPropertiesSliderChanged;
        if (_opacityEditor?.Slider != null) _opacityEditor.Slider.ValueChanged += OnPropertiesSliderChanged;

        // 직접 입력 이벤트
        if (_startTimeEditor?.ValueTextControl != null) _startTimeEditor.ValueTextControl.ValueCommitted += OnStartTimeCommitted;
        if (_durationEditor?.ValueTextControl != null) _durationEditor.ValueTextControl.ValueCommitted += OnDurationCommitted;
        if (_opacityEditor?.ValueTextControl != null) _opacityEditor.ValueTextControl.ValueCommitted += OnOpacityCommitted;

        var resetPropertiesButton = this.FindControl<Button>("ResetPropertiesButton");
        if (resetPropertiesButton != null) resetPropertiesButton.Click += OnResetPropertiesClick;
    }

    private void SyncPropertiesToClip()
    {
        _isUpdatingSliders = true;
        try
        {
            var clip = GetSelectedClip();

            if (clip == null)
            {
                if (_clipNameText != null) _clipNameText.Text = "-";
                if (_clipPathText != null) _clipPathText.Text = "-";
                SetSliderValue(_startTimeEditor?.Slider, 0);
                SetSliderValue(_durationEditor?.Slider, 1000);
                SetSliderValue(_opacityEditor?.Slider, 100);
                if (_trackIndexValueText != null) _trackIndexValueText.Text = "-";
            }
            else
            {
                if (_clipNameText != null)
                    _clipNameText.Text = System.IO.Path.GetFileName(clip.FilePath);
                if (_clipPathText != null)
                    _clipPathText.Text = clip.FilePath;

                // 슬라이더 범위를 클립에 맞게 조정
                if (_startTimeEditor?.Slider != null)
                    _startTimeEditor.Slider.Maximum = Math.Max(600000, clip.StartTimeMs + clip.DurationMs * 2);
                if (_durationEditor?.Slider != null)
                    _durationEditor.Slider.Maximum = Math.Max(600000, clip.DurationMs * 3);

                SetSliderValue(_startTimeEditor?.Slider, clip.StartTimeMs);
                SetSliderValue(_durationEditor?.Slider, clip.DurationMs);

                // Opacity: 키프레임 시스템에서 초기값 (기본 1.0 = 100%)
                var opacity = clip.OpacityKeyframes.Keyframes.Count > 0
                    ? clip.OpacityKeyframes.Keyframes[0].Value * 100.0
                    : 100.0;
                SetSliderValue(_opacityEditor?.Slider, opacity);

                if (_trackIndexValueText != null)
                    _trackIndexValueText.Text = clip.TrackIndex < 10 ? $"V{clip.TrackIndex + 1}" : $"A{clip.TrackIndex - 9}";
            }

            UpdatePropertiesValueTexts();
        }
        finally
        {
            _isUpdatingSliders = false;
        }
    }

    private void UpdatePropertiesValueTexts()
    {
        if (_startTimeEditor?.ValueTextControl != null && _startTimeEditor?.Slider != null)
            _startTimeEditor.ValueTextControl.DisplayValue = FormatTimeMs((long)_startTimeEditor.Slider.Value);
        if (_durationEditor?.ValueTextControl != null && _durationEditor?.Slider != null)
            _durationEditor.ValueTextControl.DisplayValue = FormatTimeMs((long)_durationEditor.Slider.Value);
        if (_opacityEditor?.ValueTextControl != null && _opacityEditor?.Slider != null)
            _opacityEditor.ValueTextControl.DisplayValue = $"{(int)_opacityEditor.Slider.Value}%";
    }

    private static string FormatTimeMs(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private void OnPropertiesSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        long startTimeMs = (long)(_startTimeEditor?.Slider?.Value ?? 0);
        long durationMs = (long)(_durationEditor?.Slider?.Value ?? 1000);
        double opacityPercent = _opacityEditor?.Slider?.Value ?? 100;

        UpdatePropertiesValueTexts();
        inspectorVm.ApplyPropertiesChange(clip, startTimeMs, durationMs, opacityPercent);
    }

    private void OnResetPropertiesClick(object? sender, RoutedEventArgs e)
    {
        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        inspectorVm.ResetProperties(clip);
        SetSliderValue(_opacityEditor?.Slider, 100);
        UpdatePropertiesValueTexts();
    }

    // --- Properties 직접 입력 핸들러 ---

    private void OnStartTimeCommitted(object? sender, string value)
    {
        if (TryParseTimeMs(value, out long ms) && _startTimeEditor?.Slider != null)
            _startTimeEditor.Slider.Value = ms;
    }

    private void OnDurationCommitted(object? sender, string value)
    {
        if (TryParseTimeMs(value, out long ms) && _durationEditor?.Slider != null)
            _durationEditor.Slider.Value = Math.Max(1, ms);
    }

    private void OnOpacityCommitted(object? sender, string value)
    {
        var cleaned = value.Replace("%", "").Trim();
        if (double.TryParse(cleaned, out double v) && _opacityEditor?.Slider != null)
            _opacityEditor.Slider.Value = Math.Clamp(v, 0, 100);
    }

    /// <summary>
    /// "MM:SS.mmm" 또는 숫자(ms) 파싱
    /// </summary>
    private static bool TryParseTimeMs(string text, out long ms)
    {
        ms = 0;
        text = text.Trim();

        // "MM:SS.mmm" 형식
        var colonIdx = text.IndexOf(':');
        if (colonIdx >= 0)
        {
            var parts = text.Split(':');
            if (parts.Length == 2)
            {
                var secParts = parts[1].Split('.');
                if (int.TryParse(parts[0], out int minutes) &&
                    int.TryParse(secParts[0], out int seconds))
                {
                    int millis = 0;
                    if (secParts.Length > 1)
                        int.TryParse(secParts[1].PadRight(3, '0').Substring(0, 3), out millis);
                    ms = (minutes * 60 + seconds) * 1000L + millis;
                    return true;
                }
            }
            return false;
        }

        // 순수 숫자 (ms)
        if (long.TryParse(text, out ms))
            return true;

        // 소수점 초 (예: "1.5" → 1500ms)
        if (double.TryParse(text, out double sec))
        {
            ms = (long)(sec * 1000);
            return true;
        }
        return false;
    }

    // ==================== Color ====================

    private void SetupColorSliders()
    {
        _brightnessEditor = this.FindControl<Controls.PropertyEditorControl>("BrightnessEditor");
        _contrastEditor = this.FindControl<Controls.PropertyEditorControl>("ContrastEditor");
        _saturationEditor = this.FindControl<Controls.PropertyEditorControl>("SaturationEditor");
        _temperatureEditor = this.FindControl<Controls.PropertyEditorControl>("TemperatureEditor");

        if (_brightnessEditor?.Slider != null) _brightnessEditor.Slider.ValueChanged += OnEffectSliderChanged;
        if (_contrastEditor?.Slider != null) _contrastEditor.Slider.ValueChanged += OnEffectSliderChanged;
        if (_saturationEditor?.Slider != null) _saturationEditor.Slider.ValueChanged += OnEffectSliderChanged;
        if (_temperatureEditor?.Slider != null) _temperatureEditor.Slider.ValueChanged += OnEffectSliderChanged;

        // 직접 입력 이벤트
        if (_brightnessEditor?.ValueTextControl != null) _brightnessEditor.ValueTextControl.ValueCommitted += OnColorValueCommitted;
        if (_contrastEditor?.ValueTextControl != null) _contrastEditor.ValueTextControl.ValueCommitted += OnColorValueCommitted;
        if (_saturationEditor?.ValueTextControl != null) _saturationEditor.ValueTextControl.ValueCommitted += OnColorValueCommitted;
        if (_temperatureEditor?.ValueTextControl != null) _temperatureEditor.ValueTextControl.ValueCommitted += OnColorValueCommitted;

        var resetButton = this.FindControl<Button>("ResetEffectsButton");
        if (resetButton != null) resetButton.Click += OnResetEffectsClick;
    }

    private void SyncSlidersToClip()
    {
        _isUpdatingSliders = true;
        try
        {
            var clip = GetSelectedClip();

            if (clip == null)
            {
                SetSliderValue(_brightnessEditor?.Slider, 0);
                SetSliderValue(_contrastEditor?.Slider, 0);
                SetSliderValue(_saturationEditor?.Slider, 0);
                SetSliderValue(_temperatureEditor?.Slider, 0);
            }
            else
            {
                SetSliderValue(_brightnessEditor?.Slider, clip.Brightness * 100.0);
                SetSliderValue(_contrastEditor?.Slider, clip.Contrast * 100.0);
                SetSliderValue(_saturationEditor?.Slider, clip.Saturation * 100.0);
                SetSliderValue(_temperatureEditor?.Slider, clip.Temperature * 100.0);
            }

            UpdateColorValueTexts();
        }
        finally
        {
            _isUpdatingSliders = false;
        }
    }

    private static void SetSliderValue(Slider? slider, double value)
    {
        if (slider != null)
            slider.Value = Math.Round(value);
    }

    private void UpdateColorValueTexts()
    {
        if (_brightnessEditor?.ValueTextControl != null && _brightnessEditor?.Slider != null)
            _brightnessEditor.ValueTextControl.DisplayValue = ((int)_brightnessEditor.Slider.Value).ToString();
        if (_contrastEditor?.ValueTextControl != null && _contrastEditor?.Slider != null)
            _contrastEditor.ValueTextControl.DisplayValue = ((int)_contrastEditor.Slider.Value).ToString();
        if (_saturationEditor?.ValueTextControl != null && _saturationEditor?.Slider != null)
            _saturationEditor.ValueTextControl.DisplayValue = ((int)_saturationEditor.Slider.Value).ToString();
        if (_temperatureEditor?.ValueTextControl != null && _temperatureEditor?.Slider != null)
            _temperatureEditor.ValueTextControl.DisplayValue = ((int)_temperatureEditor.Slider.Value).ToString();
    }

    private void OnEffectSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        double brightness = (_brightnessEditor?.Slider?.Value ?? 0) / 100.0;
        double contrast = (_contrastEditor?.Slider?.Value ?? 0) / 100.0;
        double saturation = (_saturationEditor?.Slider?.Value ?? 0) / 100.0;
        double temperature = (_temperatureEditor?.Slider?.Value ?? 0) / 100.0;

        UpdateColorValueTexts();
        inspectorVm.ApplyColorEffects(clip, brightness, contrast, saturation, temperature);
    }

    private void OnResetEffectsClick(object? sender, RoutedEventArgs e)
    {
        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        inspectorVm.ResetColorEffects(clip);
        SyncSlidersToClip();
    }

    private void OnColorValueCommitted(object? sender, string value)
    {
        var cleaned = value.Trim();
        if (!int.TryParse(cleaned, out int v)) return;

        Slider? slider = sender switch
        {
            Controls.EditableValueText s when s == _brightnessEditor?.ValueTextControl => _brightnessEditor?.Slider,
            Controls.EditableValueText s when s == _contrastEditor?.ValueTextControl => _contrastEditor?.Slider,
            Controls.EditableValueText s when s == _saturationEditor?.ValueTextControl => _saturationEditor?.Slider,
            Controls.EditableValueText s when s == _temperatureEditor?.ValueTextControl => _temperatureEditor?.Slider,
            _ => null
        };
        if (slider != null)
            slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
    }

    // ==================== Audio ====================

    private void SetupAudioSliders()
    {
        _volumeEditor = this.FindControl<Controls.PropertyEditorControl>("VolumeEditor");
        _speedEditor = this.FindControl<Controls.PropertyEditorControl>("SpeedEditor");
        _fadeInEditor = this.FindControl<Controls.PropertyEditorControl>("FadeInEditor");
        _fadeOutEditor = this.FindControl<Controls.PropertyEditorControl>("FadeOutEditor");

        if (_volumeEditor?.Slider != null) _volumeEditor.Slider.ValueChanged += OnAudioSliderChanged;
        if (_speedEditor?.Slider != null) _speedEditor.Slider.ValueChanged += OnAudioSliderChanged;
        if (_fadeInEditor?.Slider != null) _fadeInEditor.Slider.ValueChanged += OnAudioSliderChanged;
        if (_fadeOutEditor?.Slider != null) _fadeOutEditor.Slider.ValueChanged += OnAudioSliderChanged;

        // 직접 입력 이벤트
        if (_volumeEditor?.ValueTextControl != null) _volumeEditor.ValueTextControl.ValueCommitted += OnVolumeCommitted;
        if (_speedEditor?.ValueTextControl != null) _speedEditor.ValueTextControl.ValueCommitted += OnSpeedCommitted;
        if (_fadeInEditor?.ValueTextControl != null) _fadeInEditor.ValueTextControl.ValueCommitted += OnFadeInCommitted;
        if (_fadeOutEditor?.ValueTextControl != null) _fadeOutEditor.ValueTextControl.ValueCommitted += OnFadeOutCommitted;

        var resetAudioButton = this.FindControl<Button>("ResetAudioButton");
        if (resetAudioButton != null) resetAudioButton.Click += OnResetAudioClick;
    }

    private void SyncAudioSlidersToClip()
    {
        _isUpdatingSliders = true;
        try
        {
            var clip = GetSelectedClip();

            if (clip == null)
            {
                SetSliderValue(_volumeEditor?.Slider, 100);
                SetSliderValue(_speedEditor?.Slider, 100);
                SetSliderValue(_fadeInEditor?.Slider, 0);
                SetSliderValue(_fadeOutEditor?.Slider, 0);
            }
            else
            {
                SetSliderValue(_volumeEditor?.Slider, clip.Volume * 100.0);
                SetSliderValue(_speedEditor?.Slider, clip.Speed * 100.0);
                SetSliderValue(_fadeInEditor?.Slider, clip.FadeInMs);
                SetSliderValue(_fadeOutEditor?.Slider, clip.FadeOutMs);
            }

            UpdateAudioValueTexts();
        }
        finally
        {
            _isUpdatingSliders = false;
        }
    }

    private void UpdateAudioValueTexts()
    {
        if (_volumeEditor?.ValueTextControl != null && _volumeEditor?.Slider != null)
            _volumeEditor.ValueTextControl.DisplayValue = $"{(int)_volumeEditor.Slider.Value}%";
        if (_speedEditor?.ValueTextControl != null && _speedEditor?.Slider != null)
            _speedEditor.ValueTextControl.DisplayValue = $"{_speedEditor.Slider.Value / 100.0:F2}x";
        if (_fadeInEditor?.ValueTextControl != null && _fadeInEditor?.Slider != null)
            _fadeInEditor.ValueTextControl.DisplayValue = $"{(int)_fadeInEditor.Slider.Value}ms";
        if (_fadeOutEditor?.ValueTextControl != null && _fadeOutEditor?.Slider != null)
            _fadeOutEditor.ValueTextControl.DisplayValue = $"{(int)_fadeOutEditor.Slider.Value}ms";
    }

    private void OnAudioSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        double volume = (_volumeEditor?.Slider?.Value ?? 100) / 100.0;
        double speed = (_speedEditor?.Slider?.Value ?? 100) / 100.0;
        long fadeInMs = (long)(_fadeInEditor?.Slider?.Value ?? 0);
        long fadeOutMs = (long)(_fadeOutEditor?.Slider?.Value ?? 0);

        UpdateAudioValueTexts();
        inspectorVm.ApplyAudioSettings(clip, volume, speed, fadeInMs, fadeOutMs);
    }

    private void OnResetAudioClick(object? sender, RoutedEventArgs e)
    {
        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        inspectorVm.ResetAudioSettings(clip);
        SyncAudioSlidersToClip();
    }

    // --- Audio 직접 입력 핸들러 ---

    private void OnVolumeCommitted(object? sender, string value)
    {
        var cleaned = value.Replace("%", "").Trim();
        if (double.TryParse(cleaned, out double v) && _volumeEditor?.Slider != null)
            _volumeEditor.Slider.Value = Math.Clamp(v, 0, 200);
    }

    private void OnSpeedCommitted(object? sender, string value)
    {
        var cleaned = value.Replace("x", "").Trim();
        if (double.TryParse(cleaned, out double v) && _speedEditor?.Slider != null)
            _speedEditor.Slider.Value = Math.Clamp(v * 100, 10, 400); // 슬라이더 값은 퍼센트
    }

    private void OnFadeInCommitted(object? sender, string value)
    {
        var cleaned = value.Replace("ms", "").Trim();
        if (long.TryParse(cleaned, out long v) && _fadeInEditor?.Slider != null)
            _fadeInEditor.Slider.Value = Math.Clamp(v, 0, 5000);
    }

    private void OnFadeOutCommitted(object? sender, string value)
    {
        var cleaned = value.Replace("ms", "").Trim();
        if (long.TryParse(cleaned, out long v) && _fadeOutEditor?.Slider != null)
            _fadeOutEditor.Slider.Value = Math.Clamp(v, 0, 5000);
    }

    // ==================== Subtitle ====================

    private void SetupSubtitleControls()
    {
        _subtitleTextBox = this.FindControl<TextBox>("SubtitleTextBox");
        _fontSizeSlider = this.FindControl<Slider>("FontSizeSlider");
        _fontSizeValueText = this.FindControl<TextBlock>("FontSizeValueText");
        _subtitlePositionComboBox = this.FindControl<ComboBox>("SubtitlePositionComboBox");
        _boldToggle = this.FindControl<ToggleButton>("BoldToggle");
        _italicToggle = this.FindControl<ToggleButton>("ItalicToggle");
        _subtitleEditPanel = this.FindControl<Panel>("SubtitleEditPanel");

        if (_subtitleTextBox != null) _subtitleTextBox.TextChanged += OnSubtitleTextChanged;
        if (_fontSizeSlider != null) _fontSizeSlider.ValueChanged += OnSubtitleStyleChanged;
        if (_subtitlePositionComboBox != null) _subtitlePositionComboBox.SelectionChanged += OnSubtitleStyleSelectionChanged;
        if (_boldToggle != null) _boldToggle.IsCheckedChanged += OnSubtitleStyleToggleChanged;
        if (_italicToggle != null) _italicToggle.IsCheckedChanged += OnSubtitleStyleToggleChanged;

        var resetSubtitleButton = this.FindControl<Button>("ResetSubtitleButton");
        if (resetSubtitleButton != null) resetSubtitleButton.Click += OnResetSubtitleClick;
    }

    private void SyncSubtitleToClip()
    {
        _isUpdatingSliders = true;
        try
        {
            var subtitleClip = GetSelectedClip() as SubtitleClipModel;

            // 자막 클립 여부에 따라 편집 패널 토글
            if (_subtitleEditPanel != null)
                _subtitleEditPanel.IsVisible = subtitleClip != null;

            if (subtitleClip == null)
            {
                if (_subtitleTextBox != null) _subtitleTextBox.Text = "";
                SetSliderValue(_fontSizeSlider, 48);
                if (_subtitlePositionComboBox != null) _subtitlePositionComboBox.SelectedIndex = 2;
                if (_boldToggle != null) _boldToggle.IsChecked = false;
                if (_italicToggle != null) _italicToggle.IsChecked = false;
            }
            else
            {
                if (_subtitleTextBox != null) _subtitleTextBox.Text = subtitleClip.Text;
                SetSliderValue(_fontSizeSlider, subtitleClip.Style.FontSize);
                if (_subtitlePositionComboBox != null)
                    _subtitlePositionComboBox.SelectedIndex = (int)subtitleClip.Style.Position;
                if (_boldToggle != null) _boldToggle.IsChecked = subtitleClip.Style.IsBold;
                if (_italicToggle != null) _italicToggle.IsChecked = subtitleClip.Style.IsItalic;
            }

            UpdateFontSizeValueText();
        }
        finally
        {
            _isUpdatingSliders = false;
        }
    }

    private void UpdateFontSizeValueText()
    {
        if (_fontSizeValueText != null && _fontSizeSlider != null)
            _fontSizeValueText.Text = ((int)_fontSizeSlider.Value).ToString();
    }

    private void OnSubtitleTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var subtitleClip = GetSelectedClip() as SubtitleClipModel;
        var inspectorVm = GetInspectorVm();
        if (subtitleClip == null || inspectorVm == null || _subtitleTextBox == null) return;

        inspectorVm.ApplySubtitleText(subtitleClip, _subtitleTextBox.Text ?? "");
    }

    private void OnSubtitleStyleChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;
        ApplyCurrentSubtitleStyle();
    }

    private void OnSubtitleStyleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;
        ApplyCurrentSubtitleStyle();
    }

    private void OnSubtitleStyleToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingSliders) return;
        ApplyCurrentSubtitleStyle();
    }

    private void ApplyCurrentSubtitleStyle()
    {
        var subtitleClip = GetSelectedClip() as SubtitleClipModel;
        var inspectorVm = GetInspectorVm();
        if (subtitleClip == null || inspectorVm == null) return;

        double fontSize = _fontSizeSlider?.Value ?? 48;
        var position = (SubtitlePosition)(_subtitlePositionComboBox?.SelectedIndex ?? 2);
        bool isBold = _boldToggle?.IsChecked ?? false;
        bool isItalic = _italicToggle?.IsChecked ?? false;

        UpdateFontSizeValueText();
        inspectorVm.ApplySubtitleStyle(subtitleClip, fontSize, position, isBold, isItalic);
    }

    private void OnResetSubtitleClick(object? sender, RoutedEventArgs e)
    {
        var subtitleClip = GetSelectedClip() as SubtitleClipModel;
        var inspectorVm = GetInspectorVm();
        if (subtitleClip == null || inspectorVm == null) return;

        inspectorVm.ResetSubtitleStyle(subtitleClip);
        SyncSubtitleToClip();
    }

    // ==================== Transition ====================

    private void SetupTransitionControls()
    {
        _transitionTypeComboBox = this.FindControl<ComboBox>("TransitionTypeComboBox");
        if (_transitionTypeComboBox != null)
            _transitionTypeComboBox.SelectionChanged += OnTransitionTypeChanged;

        var resetTransitionButton = this.FindControl<Button>("ResetTransitionButton");
        if (resetTransitionButton != null)
            resetTransitionButton.Click += OnResetTransitionClick;
    }

    private void SyncTransitionToClip()
    {
        _isUpdatingSliders = true;
        try
        {
            var clip = GetSelectedClip();

            if (_transitionTypeComboBox != null)
            {
                _transitionTypeComboBox.SelectedIndex = clip != null ? (int)clip.TransitionType : 0;
            }
        }
        finally
        {
            _isUpdatingSliders = false;
        }
    }

    private void OnTransitionTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null || _transitionTypeComboBox == null) return;

        var transitionType = (TransitionType)_transitionTypeComboBox.SelectedIndex;
        inspectorVm.ApplyTransition(clip, transitionType);
    }

    private void OnResetTransitionClick(object? sender, RoutedEventArgs e)
    {
        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        inspectorVm.ResetTransition(clip);
        SyncTransitionToClip();
    }
}

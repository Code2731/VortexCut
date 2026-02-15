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
    private TextBlock? _workspaceTitle;

    // Color 슬라이더
    private Slider? _brightnessSlider;
    private Slider? _contrastSlider;
    private Slider? _saturationSlider;
    private Slider? _temperatureSlider;
    private TextBlock? _brightnessValueText;
    private TextBlock? _contrastValueText;
    private TextBlock? _saturationValueText;
    private TextBlock? _temperatureValueText;

    // Audio 슬라이더
    private Slider? _volumeSlider;
    private Slider? _speedSlider;
    private Slider? _fadeInSlider;
    private Slider? _fadeOutSlider;
    private TextBlock? _volumeValueText;
    private TextBlock? _speedValueText;
    private TextBlock? _fadeInValueText;
    private TextBlock? _fadeOutValueText;

    // Transition
    private ComboBox? _transitionTypeComboBox;

    // Properties (Editing)
    private TextBlock? _clipNameText;
    private TextBlock? _clipPathText;
    private Slider? _startTimeSlider;
    private Slider? _durationSlider;
    private Slider? _opacitySlider;
    private TextBlock? _startTimeValueText;
    private TextBlock? _durationValueText;
    private TextBlock? _trackIndexValueText;
    private TextBlock? _opacityValueText;

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
        _workspaceTitle = this.FindControl<TextBlock>("WorkspaceTitle");

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

        var workspace = mainVm.ActiveWorkspace;

        if (_propertiesPanel != null) _propertiesPanel.IsVisible = workspace == "Editing";
        if (_colorPanel != null) _colorPanel.IsVisible = workspace == "Color";
        if (_audioPanel != null) _audioPanel.IsVisible = workspace == "Audio";
        if (_effectsPanel != null) _effectsPanel.IsVisible = workspace == "Effects";

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
        _startTimeSlider = this.FindControl<Slider>("StartTimeSlider");
        _durationSlider = this.FindControl<Slider>("DurationSlider");
        _opacitySlider = this.FindControl<Slider>("OpacitySlider");
        _startTimeValueText = this.FindControl<TextBlock>("StartTimeValueText");
        _durationValueText = this.FindControl<TextBlock>("DurationValueText");
        _trackIndexValueText = this.FindControl<TextBlock>("TrackIndexValueText");
        _opacityValueText = this.FindControl<TextBlock>("OpacityValueText");

        if (_startTimeSlider != null) _startTimeSlider.ValueChanged += OnPropertiesSliderChanged;
        if (_durationSlider != null) _durationSlider.ValueChanged += OnPropertiesSliderChanged;
        if (_opacitySlider != null) _opacitySlider.ValueChanged += OnPropertiesSliderChanged;

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
                SetSliderValue(_startTimeSlider, 0);
                SetSliderValue(_durationSlider, 1000);
                SetSliderValue(_opacitySlider, 100);
                if (_trackIndexValueText != null) _trackIndexValueText.Text = "-";
            }
            else
            {
                if (_clipNameText != null)
                    _clipNameText.Text = System.IO.Path.GetFileName(clip.FilePath);
                if (_clipPathText != null)
                    _clipPathText.Text = clip.FilePath;

                // 슬라이더 범위를 클립에 맞게 조정
                if (_startTimeSlider != null)
                    _startTimeSlider.Maximum = Math.Max(600000, clip.StartTimeMs + clip.DurationMs * 2);
                if (_durationSlider != null)
                    _durationSlider.Maximum = Math.Max(600000, clip.DurationMs * 3);

                SetSliderValue(_startTimeSlider, clip.StartTimeMs);
                SetSliderValue(_durationSlider, clip.DurationMs);

                // Opacity: 키프레임 시스템에서 초기값 (기본 1.0 = 100%)
                var opacity = clip.OpacityKeyframes.Keyframes.Count > 0
                    ? clip.OpacityKeyframes.Keyframes[0].Value * 100.0
                    : 100.0;
                SetSliderValue(_opacitySlider, opacity);

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
        if (_startTimeValueText != null && _startTimeSlider != null)
            _startTimeValueText.Text = FormatTimeMs((long)_startTimeSlider.Value);
        if (_durationValueText != null && _durationSlider != null)
            _durationValueText.Text = FormatTimeMs((long)_durationSlider.Value);
        if (_opacityValueText != null && _opacitySlider != null)
            _opacityValueText.Text = $"{(int)_opacitySlider.Value}%";
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

        long startTimeMs = (long)(_startTimeSlider?.Value ?? 0);
        long durationMs = (long)(_durationSlider?.Value ?? 1000);
        double opacityPercent = _opacitySlider?.Value ?? 100;

        UpdatePropertiesValueTexts();
        inspectorVm.ApplyPropertiesChange(clip, startTimeMs, durationMs, opacityPercent);
    }

    private void OnResetPropertiesClick(object? sender, RoutedEventArgs e)
    {
        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        inspectorVm.ResetProperties(clip);
        SetSliderValue(_opacitySlider, 100);
        UpdatePropertiesValueTexts();
    }

    // ==================== Color ====================

    private void SetupColorSliders()
    {
        _brightnessSlider = this.FindControl<Slider>("BrightnessSlider");
        _contrastSlider = this.FindControl<Slider>("ContrastSlider");
        _saturationSlider = this.FindControl<Slider>("SaturationSlider");
        _temperatureSlider = this.FindControl<Slider>("TemperatureSlider");
        _brightnessValueText = this.FindControl<TextBlock>("BrightnessValueText");
        _contrastValueText = this.FindControl<TextBlock>("ContrastValueText");
        _saturationValueText = this.FindControl<TextBlock>("SaturationValueText");
        _temperatureValueText = this.FindControl<TextBlock>("TemperatureValueText");

        if (_brightnessSlider != null) _brightnessSlider.ValueChanged += OnEffectSliderChanged;
        if (_contrastSlider != null) _contrastSlider.ValueChanged += OnEffectSliderChanged;
        if (_saturationSlider != null) _saturationSlider.ValueChanged += OnEffectSliderChanged;
        if (_temperatureSlider != null) _temperatureSlider.ValueChanged += OnEffectSliderChanged;

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
                SetSliderValue(_brightnessSlider, 0);
                SetSliderValue(_contrastSlider, 0);
                SetSliderValue(_saturationSlider, 0);
                SetSliderValue(_temperatureSlider, 0);
            }
            else
            {
                SetSliderValue(_brightnessSlider, clip.Brightness * 100.0);
                SetSliderValue(_contrastSlider, clip.Contrast * 100.0);
                SetSliderValue(_saturationSlider, clip.Saturation * 100.0);
                SetSliderValue(_temperatureSlider, clip.Temperature * 100.0);
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
        if (_brightnessValueText != null && _brightnessSlider != null)
            _brightnessValueText.Text = ((int)_brightnessSlider.Value).ToString();
        if (_contrastValueText != null && _contrastSlider != null)
            _contrastValueText.Text = ((int)_contrastSlider.Value).ToString();
        if (_saturationValueText != null && _saturationSlider != null)
            _saturationValueText.Text = ((int)_saturationSlider.Value).ToString();
        if (_temperatureValueText != null && _temperatureSlider != null)
            _temperatureValueText.Text = ((int)_temperatureSlider.Value).ToString();
    }

    private void OnEffectSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        double brightness = (_brightnessSlider?.Value ?? 0) / 100.0;
        double contrast = (_contrastSlider?.Value ?? 0) / 100.0;
        double saturation = (_saturationSlider?.Value ?? 0) / 100.0;
        double temperature = (_temperatureSlider?.Value ?? 0) / 100.0;

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

    // ==================== Audio ====================

    private void SetupAudioSliders()
    {
        _volumeSlider = this.FindControl<Slider>("VolumeSlider");
        _speedSlider = this.FindControl<Slider>("SpeedSlider");
        _fadeInSlider = this.FindControl<Slider>("FadeInSlider");
        _fadeOutSlider = this.FindControl<Slider>("FadeOutSlider");
        _volumeValueText = this.FindControl<TextBlock>("VolumeValueText");
        _speedValueText = this.FindControl<TextBlock>("SpeedValueText");
        _fadeInValueText = this.FindControl<TextBlock>("FadeInValueText");
        _fadeOutValueText = this.FindControl<TextBlock>("FadeOutValueText");

        if (_volumeSlider != null) _volumeSlider.ValueChanged += OnAudioSliderChanged;
        if (_speedSlider != null) _speedSlider.ValueChanged += OnAudioSliderChanged;
        if (_fadeInSlider != null) _fadeInSlider.ValueChanged += OnAudioSliderChanged;
        if (_fadeOutSlider != null) _fadeOutSlider.ValueChanged += OnAudioSliderChanged;

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
                SetSliderValue(_volumeSlider, 100);
                SetSliderValue(_speedSlider, 100);
                SetSliderValue(_fadeInSlider, 0);
                SetSliderValue(_fadeOutSlider, 0);
            }
            else
            {
                SetSliderValue(_volumeSlider, clip.Volume * 100.0);
                SetSliderValue(_speedSlider, clip.Speed * 100.0);
                SetSliderValue(_fadeInSlider, clip.FadeInMs);
                SetSliderValue(_fadeOutSlider, clip.FadeOutMs);
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
        if (_volumeValueText != null && _volumeSlider != null)
            _volumeValueText.Text = $"{(int)_volumeSlider.Value}%";
        if (_speedValueText != null && _speedSlider != null)
            _speedValueText.Text = $"{_speedSlider.Value / 100.0:F2}x";
        if (_fadeInValueText != null && _fadeInSlider != null)
            _fadeInValueText.Text = $"{(int)_fadeInSlider.Value}ms";
        if (_fadeOutValueText != null && _fadeOutSlider != null)
            _fadeOutValueText.Text = $"{(int)_fadeOutSlider.Value}ms";
    }

    private void OnAudioSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var clip = GetSelectedClip();
        var inspectorVm = GetInspectorVm();
        if (clip == null || inspectorVm == null) return;

        double volume = (_volumeSlider?.Value ?? 100) / 100.0;
        double speed = (_speedSlider?.Value ?? 100) / 100.0;
        long fadeInMs = (long)(_fadeInSlider?.Value ?? 0);
        long fadeOutMs = (long)(_fadeOutSlider?.Value ?? 0);

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

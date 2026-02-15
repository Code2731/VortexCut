using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using VortexCut.UI.ViewModels;

namespace VortexCut.UI.Views;

public partial class InspectorView : UserControl
{
    // Color 탭 슬라이더
    private Slider? _brightnessSlider;
    private Slider? _contrastSlider;
    private Slider? _saturationSlider;
    private Slider? _temperatureSlider;
    private TextBlock? _brightnessValueText;
    private TextBlock? _contrastValueText;
    private TextBlock? _saturationValueText;
    private TextBlock? _temperatureValueText;

    // Audio 탭 슬라이더
    private Slider? _volumeSlider;
    private Slider? _speedSlider;
    private Slider? _fadeInSlider;
    private Slider? _fadeOutSlider;
    private TextBlock? _volumeValueText;
    private TextBlock? _speedValueText;
    private TextBlock? _fadeInValueText;
    private TextBlock? _fadeOutValueText;

    // 프로그래밍적 슬라이더 값 설정 시 이벤트 무시용 플래그
    private bool _isUpdatingSliders;

    public InspectorView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SetupColorSliders();
        SetupAudioSliders();
    }

    // ==================== Color 탭 ====================

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

        // SelectedClip 변경 감지 → 슬라이더 값 동기화
        if (DataContext is MainViewModel mainVm)
        {
            mainVm.Timeline.PropertyChanged += OnTimelinePropertyChanged;
        }
    }

    private void OnTimelinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "SelectedClip")
        {
            SyncSlidersToClip();
            SyncAudioSlidersToClip();
        }
    }

    private void SyncSlidersToClip()
    {
        _isUpdatingSliders = true;
        try
        {
            var mainVm = DataContext as MainViewModel;
            var clip = mainVm?.Timeline.SelectedClip;

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

        var mainVm = DataContext as MainViewModel;
        var clip = mainVm?.Timeline.SelectedClip;
        if (clip == null || mainVm == null) return;

        double brightness = (_brightnessSlider?.Value ?? 0) / 100.0;
        double contrast = (_contrastSlider?.Value ?? 0) / 100.0;
        double saturation = (_saturationSlider?.Value ?? 0) / 100.0;
        double temperature = (_temperatureSlider?.Value ?? 0) / 100.0;

        clip.Brightness = brightness;
        clip.Contrast = contrast;
        clip.Saturation = saturation;
        clip.Temperature = temperature;

        UpdateColorValueTexts();

        mainVm.ProjectService.SetClipEffects(
            clip.Id,
            (float)brightness,
            (float)contrast,
            (float)saturation,
            (float)temperature);

        mainVm.Preview.RenderFrameAsync(mainVm.Timeline.CurrentTimeMs);
    }

    private void OnResetEffectsClick(object? sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as MainViewModel;
        var clip = mainVm?.Timeline.SelectedClip;
        if (clip == null || mainVm == null) return;

        clip.Brightness = 0;
        clip.Contrast = 0;
        clip.Saturation = 0;
        clip.Temperature = 0;

        SyncSlidersToClip();

        mainVm.ProjectService.SetClipEffects(clip.Id, 0, 0, 0, 0);
        mainVm.Preview.RenderFrameAsync(mainVm.Timeline.CurrentTimeMs);
    }

    // ==================== Audio 탭 ====================

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

    /// <summary>
    /// 선택된 클립의 오디오 값을 슬라이더에 동기화
    /// </summary>
    private void SyncAudioSlidersToClip()
    {
        _isUpdatingSliders = true;
        try
        {
            var mainVm = DataContext as MainViewModel;
            var clip = mainVm?.Timeline.SelectedClip;

            if (clip == null)
            {
                SetSliderValue(_volumeSlider, 100);
                SetSliderValue(_speedSlider, 100);
                SetSliderValue(_fadeInSlider, 0);
                SetSliderValue(_fadeOutSlider, 0);
            }
            else
            {
                // Volume: 0.0~2.0 → 0~200
                SetSliderValue(_volumeSlider, clip.Volume * 100.0);
                // Speed: 0.25~4.0 → 25~400
                SetSliderValue(_speedSlider, clip.Speed * 100.0);
                // Fade: 직접 ms
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

    /// <summary>
    /// Audio 슬라이더 값 변경 → ClipModel + Rust FFI 전달
    /// </summary>
    private void OnAudioSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliders) return;

        var mainVm = DataContext as MainViewModel;
        var clip = mainVm?.Timeline.SelectedClip;
        if (clip == null || mainVm == null) return;

        // 슬라이더 → ClipModel
        double volume = (_volumeSlider?.Value ?? 100) / 100.0;   // 0~200 → 0.0~2.0
        double speed = (_speedSlider?.Value ?? 100) / 100.0;      // 25~400 → 0.25~4.0
        long fadeInMs = (long)(_fadeInSlider?.Value ?? 0);
        long fadeOutMs = (long)(_fadeOutSlider?.Value ?? 0);

        clip.Volume = volume;
        clip.Speed = speed;
        clip.FadeInMs = fadeInMs;
        clip.FadeOutMs = fadeOutMs;

        UpdateAudioValueTexts();

        // Rust Timeline에 전달
        mainVm.ProjectService.SetClipVolume(clip.Id, (float)volume);
        mainVm.ProjectService.SetClipSpeed(clip.Id, speed);
        mainVm.ProjectService.SetClipFade(clip.Id, fadeInMs, fadeOutMs);

        // Speed 변경 시 프리뷰 프레임 캐시 무효화 + 갱신
        mainVm.ProjectService.ClearRenderCache();
        mainVm.Preview.RenderFrameAsync(mainVm.Timeline.CurrentTimeMs);
    }

    /// <summary>
    /// Reset All 버튼 → 오디오 설정 기본값으로 초기화
    /// </summary>
    private void OnResetAudioClick(object? sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as MainViewModel;
        var clip = mainVm?.Timeline.SelectedClip;
        if (clip == null || mainVm == null) return;

        clip.Volume = 1.0;
        clip.Speed = 1.0;
        clip.FadeInMs = 0;
        clip.FadeOutMs = 0;

        SyncAudioSlidersToClip();

        mainVm.ProjectService.SetClipVolume(clip.Id, 1.0f);
        mainVm.ProjectService.SetClipSpeed(clip.Id, 1.0);
        mainVm.ProjectService.SetClipFade(clip.Id, 0, 0);

        mainVm.ProjectService.ClearRenderCache();
        mainVm.Preview.RenderFrameAsync(mainVm.Timeline.CurrentTimeMs);
    }
}

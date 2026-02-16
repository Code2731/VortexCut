namespace VortexCut.UI.Services;

/// <summary>
/// 미리보기 품질 설정 (프레임 스킵 / Temporal Compression)
/// 5fps: 저사양, 10fps: 기본(권장), 30fps: 고품질
/// </summary>
public static class PreviewSettings
{
    /// <summary>
    /// 미리보기 목표 FPS (5, 10, 30)
    /// </summary>
    public static double PreviewFps { get; private set; } = 30.0;

    /// <summary>
    /// 설정 변경 시 발생 (타이머 간격 갱신용)
    /// </summary>
    public static event Action? PreviewFpsChanged;

    /// <summary>
    /// 미리보기 FPS 설정 (5, 10, 30 권장)
    /// </summary>
    public static void SetPreviewFps(double fps)
    {
        if (fps is <= 0 or > 60) return;
        if (Math.Abs(PreviewFps - fps) < 0.01) return;
        PreviewFps = fps;
        PreviewFpsChanged?.Invoke();
    }
}

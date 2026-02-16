using System.Diagnostics;

namespace VortexCut.UI.Services;

/// <summary>
/// 프리뷰/Filmstrip용 저해상도 Proxy 비디오를 생성하는 간단한 서비스.
/// 현재는 외부 ffmpeg 바이너리를 호출하는 방식으로 동작하며,
/// 실패 시 null을 반환하고 원본 파일을 그대로 사용하도록 한다.
/// </summary>
public class ProxyService
{
    /// <summary>
    /// 주어진 원본 비디오 파일에 대해 Proxy 파일을 생성하고 경로를 반환한다.
    /// 실패 시 null을 반환한다.
    /// </summary>
    public async Task<string?> CreateProxyAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return null;

            var sourceDir = Path.GetDirectoryName(sourcePath) ?? "";
            var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
            var proxyDir = Path.Combine(sourceDir, "vortexcut_proxies");
            Directory.CreateDirectory(proxyDir);

            var proxyPath = Path.Combine(proxyDir, $"{sourceName}_proxy.mp4");

            // 이미 Proxy가 있으면 재사용
            if (File.Exists(proxyPath))
                return proxyPath;

            // ffmpeg 호출: 해상도 1/2, 적당한 비트레이트로 H.264 인코딩
            // 예: ffmpeg -y -i input -vf scale=iw/2:ih/2 -c:v libx264 -preset veryfast -crf 23 -c:a copy proxy.mp4
            // -g 30 -keyint_min 30: 1초(30fps)마다 키프레임 강제 → 스크럽 seek 속도 대폭 개선
            var args = $"-y -i \"{sourcePath}\" -vf scale=iw/2:ih/2 -c:v libx264 -preset veryfast -crf 23 -g 30 -keyint_min 30 -c:a copy \"{proxyPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            process.Start();

            // 간단한 대기 루프 + 취소 지원
            while (!process.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // 무시
                    }
                    return null;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (process.ExitCode != 0)
            {
                var err = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                Debug.WriteLine($"ProxyService: ffmpeg exited with code {process.ExitCode}: {err}");
                return null;
            }

            return File.Exists(proxyPath) ? proxyPath : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProxyService.CreateProxyAsync ERROR: {ex}");
            return null;
        }
    }
}


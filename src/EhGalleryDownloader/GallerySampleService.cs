using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EhGalleryDownloader;

public static class GallerySampleService
{
    public static async Task<IReadOnlyList<Uri>> GetSampleUrlsAsync(
        string enginePath,
        string galleryUrl,
        DownloadOptions options,
        CancellationToken cancellationToken)
        => await GetSampleUrlsAsync(
            enginePath,
            galleryUrl,
            options,
            maximumItems: 40,
            cancellationToken: cancellationToken);

    public static async Task<IReadOnlyList<Uri>> GetSampleUrlsAsync(
        string enginePath,
        string galleryUrl,
        DownloadOptions options,
        int maximumItems,
        CancellationToken cancellationToken)
    {
        maximumItems = Math.Clamp(maximumItems, 1, 40);
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var configPath = Path.Combine(
            SettingsStore.DataDirectory, $"probe-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                configPath,
                BuildConfig(options),
                new UTF8Encoding(false),
                cancellationToken);

            var info = new ProcessStartInfo
            {
                FileName = enginePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = GalleryDlProcessEncoding.Current,
                StandardErrorEncoding = GalleryDlProcessEncoding.Current
            };
            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.Environment["PYTHONLEGACYWINDOWSSTDIO"] = "0";
            Add(info, "--config-ignore", "--config-json", configPath);
            Add(info, "--no-input", "--no-colors", "--get-urls", "--range",
                $"1-{maximumItems}");
            Add(info, "--http-timeout", "25", "--retries", "1");
            if (options.UseProxy) Add(info, "--proxy", options.ProxyUrl);
            if (options.UseBrowserCookies)
                Add(info, "--cookies-from-browser", options.Browser);
            if (options.ForceIpv4) Add(info, "--force-ipv4");
            Add(info, "--option",
                $"extractor.exhentai.original={options.DownloadOriginal.ToString().ToLowerInvariant()}");
            info.ArgumentList.Add(galleryUrl);

            using var process = new Process { StartInfo = info };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
            });
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(ToFriendlyExtractorError(stderr));

            var urls = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => Uri.TryCreate(line, UriKind.Absolute, out var uri) ? uri : null)
                .Where(uri => uri is { Scheme: "http" or "https" })
                .Cast<Uri>()
                .ToList();
            if (urls.Count == 0)
                throw new InvalidOperationException(
                    "没有取得可用于测试的图片地址。请先确认画廊链接和 Cookie 可以正常使用。");

            var hath = urls
                .Where(uri => uri.Host.EndsWith(".hath.network", StringComparison.OrdinalIgnoreCase))
                .GroupBy(uri => uri.Host, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(5)
                .ToList();
            if (hath.Count >= 2) return hath;

            return urls
                .GroupBy(uri => uri.Host, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(5)
                .ToList();
        }
        finally
        {
            try { File.Delete(configPath); } catch { }
        }
    }

    private static string BuildConfig(DownloadOptions options)
    {
        var config = new Dictionary<string, object>();
        if (options.LoginMode == "manual"
            && CookieParser.TryParse(options.ManualCookie, out var cookies, out _))
        {
            config["extractor"] = new Dictionary<string, object>
            {
                ["exhentai"] = new Dictionary<string, object>
                {
                    ["cookies"] = cookies,
                    ["cookies-update"] = false
                }
            };
        }
        return JsonSerializer.Serialize(config);
    }

    private static string ToFriendlyExtractorError(string stderr)
    {
        if (stderr.Contains("KeyError - 'i3'", StringComparison.OrdinalIgnoreCase))
            return "当前下载内核出现兼容错误（KeyError i3），无法取得测速图片。请先更新内核；这不是 Clash 端口故障。";
        if (stderr.Contains("UNEXPECTED_EOF", StringComparison.OrdinalIgnoreCase)
            || (stderr.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                && stderr.Contains("EOF", StringComparison.OrdinalIgnoreCase)))
            return "当前节点连接画廊图片服务器时被中途断开。Clash 本身正在运行，但这条节点到 H@H 图片服务器不稳定。";
        if (stderr.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "读取画廊信息超时。Clash 本身可能正常，但当前节点到画廊服务器的线路较慢。";
        if (stderr.Contains("AuthenticationError", StringComparison.OrdinalIgnoreCase))
            return "当前 Cookie 无法读取这个画廊，请重新验证登录。";
        if (stderr.Contains("cookies", StringComparison.OrdinalIgnoreCase))
            return "读取浏览器登录信息失败。可以改用手动粘贴 Cookie。";
        if (stderr.Contains("unsupported URL", StringComparison.OrdinalIgnoreCase))
            return "链接不受支持，请粘贴画廊首页链接。";
        var finalLine = stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(finalLine)
            ? "下载内核没有返回图片地址。请验证 Cookie，并确认画廊能在浏览器中打开。"
            : $"下载内核无法取得图片地址：{finalLine}";
    }

    private static void Add(ProcessStartInfo info, params string[] values)
    {
        foreach (var value in values) info.ArgumentList.Add(value);
    }
}

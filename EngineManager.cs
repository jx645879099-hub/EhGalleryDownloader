using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EhGalleryDownloader;

public enum EngineUpdateFailureKind
{
    RateLimited,
    ProxyUnavailable,
    TimedOut,
    ReleaseUnavailable,
    DownloadRejected,
    InvalidDownload,
    Unknown
}

public sealed class EngineUpdateException : Exception
{
    public EngineUpdateException(
        EngineUpdateFailureKind kind,
        string message,
        Exception? innerException = null) : base(message, innerException) =>
        Kind = kind;

    public EngineUpdateFailureKind Kind { get; }
}

public sealed record EngineUpdateInfo(
    bool EngineMissing,
    bool IsLatest,
    string? LocalVersion,
    string RemoteVersion,
    string DownloadUrl,
    string? Sha256Digest);

public sealed class EngineManager
{
    private const string LatestReleaseUrl =
        "https://github.com/gdl-org/builds/releases/latest";
    private const string ExpandedAssetsBaseUrl =
        "https://github.com/gdl-org/builds/releases/expanded_assets/";

    public string BundledEnginePath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "gallery-dl.exe");

    public string? FindEngine()
    {
        var candidates = new[]
        {
            BundledEnginePath,
            Path.Combine(AppContext.BaseDirectory, "gallery-dl.exe")
        };

        foreach (var path in candidates)
            if (File.Exists(path))
                return path;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "gallery-dl.exe",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            var result = process?.StandardOutput.ReadLine();
            process?.WaitForExit(3000);
            if (!string.IsNullOrWhiteSpace(result) && File.Exists(result))
                return result;
        }
        catch { }

        return null;
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default) =>
        await GetVersionOfAsync(FindEngine(), cancellationToken).ConfigureAwait(false);

    public async Task<EngineUpdateInfo> CheckForUpdateAsync(
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient(proxyUrl, allowAutoRedirect: false, TimeSpan.FromSeconds(30));
            using var latestResponse = await client.GetAsync(
                LatestReleaseUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            EnsureUpdateResponse(latestResponse, "读取最新版信息");

            var tag = ExtractReleaseTag(latestResponse);
            if (string.IsNullOrWhiteSpace(tag))
                throw new EngineUpdateException(
                    EngineUpdateFailureKind.ReleaseUnavailable,
                    "官方发布页没有返回可识别的最新版编号。");

            using var assetsResponse = await client.GetAsync(
                ExpandedAssetsBaseUrl + Uri.EscapeDataString(tag),
                cancellationToken).ConfigureAwait(false);
            EnsureUpdateResponse(assetsResponse, "读取 Windows 内核信息");
            var html = await assetsResponse.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var asset = ReleaseAssetParser.Parse(html, tag);

            var localPath = FindEngine();
            var localVersion = await GetVersionOfAsync(localPath, cancellationToken)
                .ConfigureAwait(false);
            var isLatest = !string.IsNullOrWhiteSpace(localVersion)
                           && localVersion.Contains(tag, StringComparison.OrdinalIgnoreCase);
            if (!isLatest && localPath is not null && asset.Sha256Digest is not null)
            {
                var localHash = await ComputeSha256Async(localPath, cancellationToken)
                    .ConfigureAwait(false);
                isLatest = localHash.Equals(asset.Sha256Digest, StringComparison.OrdinalIgnoreCase);
            }

            return new EngineUpdateInfo(
                localPath is null,
                isLatest,
                localVersion,
                tag,
                asset.DownloadUrl,
                asset.Sha256Digest);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EngineUpdateException(
                EngineUpdateFailureKind.TimedOut,
                "连接官方发布页超时。");
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase))
        {
            throw new EngineUpdateException(
                EngineUpdateFailureKind.ProxyUnavailable,
                "无法连接当前填写的本地代理地址。",
                ex);
        }
        catch (EngineUpdateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EngineUpdateException(
                EngineUpdateFailureKind.Unknown,
                $"检查更新失败：{ex.GetBaseException().Message}",
                ex);
        }
    }

    public async Task<string> InstallAsync(
        EngineUpdateInfo update,
        string? proxyUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BundledEnginePath)!);
        var tempPath = BundledEnginePath + ".update.exe";

        try
        {
            using var client = CreateClient(
                proxyUrl,
                allowAutoRedirect: true,
                TimeSpan.FromMinutes(8));
            using var response = await client.GetAsync(
                update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            EnsureUpdateResponse(response, "下载 Windows 内核");

            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var output = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(
                        buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                        progress?.Report(received * 100d / total.Value);
                }
            }

            var info = new FileInfo(tempPath);
            if (info.Length < 1024 * 1024)
                throw new EngineUpdateException(
                    EngineUpdateFailureKind.InvalidDownload,
                    "下载到的文件过小，不像完整的 gallery-dl 内核。");

            if (!string.IsNullOrWhiteSpace(update.Sha256Digest))
            {
                var downloadedHash = await ComputeSha256Async(tempPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!downloadedHash.Equals(
                        update.Sha256Digest, StringComparison.OrdinalIgnoreCase))
                    throw new EngineUpdateException(
                        EngineUpdateFailureKind.InvalidDownload,
                        "下载文件的完整性校验失败，原来的内核没有被覆盖。");
            }

            var downloadedVersion = await GetVersionOfAsync(tempPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloadedVersion))
                throw new EngineUpdateException(
                    EngineUpdateFailureKind.InvalidDownload,
                    "下载文件无法启动，原来的内核没有被覆盖。");

            File.Move(tempPath, BundledEnginePath, true);
            progress?.Report(100);
            return BundledEnginePath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EngineUpdateException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase))
        {
            throw new EngineUpdateException(
                EngineUpdateFailureKind.ProxyUnavailable,
                "无法连接当前填写的本地代理地址。",
                ex);
        }
        catch (Exception ex)
        {
            throw new EngineUpdateException(
                EngineUpdateFailureKind.Unknown,
                $"下载或安装内核失败：{ex.GetBaseException().Message}",
                ex);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static async Task<string?> GetVersionOfAsync(
        string? enginePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath))
            return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = enginePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 ? output.Trim() : null;
    }

    private static string ExtractReleaseTag(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null && response.RequestMessage?.RequestUri is { } finalUri)
            location = finalUri;
        var text = location?.ToString() ?? "";
        var marker = "/releases/tag/";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? ""
            : Uri.UnescapeDataString(text[(index + marker.Length)..].Trim('/'));
    }

    private static void EnsureUpdateResponse(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode
            || response.StatusCode is HttpStatusCode.Found
                or HttpStatusCode.MovedPermanently
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect)
            return;

        if (response.StatusCode is HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests)
            throw new EngineUpdateException(
                EngineUpdateFailureKind.RateLimited,
                $"{action}时，发布服务器暂时限制了请求（{(int)response.StatusCode}）。");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new EngineUpdateException(
                EngineUpdateFailureKind.ReleaseUnavailable,
                $"{action}时没有找到对应文件，可能正在发布新版本。");

        throw new EngineUpdateException(
            EngineUpdateFailureKind.DownloadRejected,
            $"{action}失败，服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
    }

    private static HttpClient CreateClient(
        string? proxyUrl,
        bool allowAutoRedirect,
        TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EhGalleryDownloader/1.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/octet-stream;q=0.9,*/*;q=0.8");
        return client;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

public sealed record ReleaseAsset(string DownloadUrl, string? Sha256Digest);

public static class ReleaseAssetParser
{
    private static readonly Regex AssetLinkRegex = new(
        "href=\"(?<href>[^\"]*/releases/download/[^\"]*/(?<name>[^\"]+\\.exe))\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DigestRegex = new(
        "sha256:(?<digest>[0-9a-f]{64})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ReleaseAsset Parse(string html, string tag)
    {
        var matches = AssetLinkRegex.Matches(html)
            .Cast<Match>()
            .Select(match => new
            {
                Match = match,
                Name = WebUtility.HtmlDecode(match.Groups["name"].Value),
                Href = WebUtility.HtmlDecode(match.Groups["href"].Value)
            })
            .ToList();
        var selected = matches.FirstOrDefault(item =>
                           item.Name.Equals(
                               "gallery-dl_windows.exe", StringComparison.OrdinalIgnoreCase))
                       ?? matches.FirstOrDefault(item =>
                           item.Name.Equals(
                               "gallery-dl.exe", StringComparison.OrdinalIgnoreCase))
                       ?? matches.FirstOrDefault(item =>
                           item.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                           && item.Name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                           && !item.Name.Contains("x86", StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new EngineUpdateException(
                EngineUpdateFailureKind.ReleaseUnavailable,
                $"官方版本 {tag} 中暂时没有找到 64 位 Windows 内核。");

        var digestWindowLength = Math.Min(1200, html.Length - selected.Match.Index);
        var digestWindow = html.Substring(selected.Match.Index, digestWindowLength);
        var digestMatch = DigestRegex.Match(digestWindow);
        var digest = digestMatch.Success
            ? digestMatch.Groups["digest"].Value
            : null;
        var downloadUrl = selected.Href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? selected.Href
            : "https://github.com" + selected.Href;
        return new ReleaseAsset(downloadUrl, digest);
    }
}

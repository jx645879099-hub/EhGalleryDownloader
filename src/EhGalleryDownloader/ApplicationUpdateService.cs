using System.Net;
using System.Net.Http;
using System.Reflection;

namespace EhGalleryDownloader;

public sealed record ApplicationUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string Tag,
    string ReleaseUrl,
    bool IsUpdateAvailable);

public static class ApplicationUpdateService
{
    private const string LatestUrl =
        "https://github.com/jx645879099-hub/EhGalleryDownloader/releases/latest";

    public static async Task<ApplicationUpdateInfo> CheckAsync(
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = !string.IsNullOrWhiteSpace(proxyUrl)
        };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EhGalleryDownloader/1.1");
        using var response = await client.GetAsync(
            LatestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var location = response.Headers.Location?.ToString() ?? "";
        if (location.Length == 0 && response.IsSuccessStatusCode)
            location = response.RequestMessage?.RequestUri?.ToString() ?? "";
        const string marker = "/releases/tag/";
        var markerIndex = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            throw new InvalidOperationException("GitHub 没有返回可识别的软件版本。请稍后再试。");
        var tag = Uri.UnescapeDataString(location[(markerIndex + marker.Length)..].Trim('/'));
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest))
            throw new InvalidOperationException($"无法识别远程版本号：{tag}");
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        return new ApplicationUpdateInfo(
            current,
            latest,
            tag,
            $"https://github.com/jx645879099-hub/EhGalleryDownloader/releases/tag/{tag}",
            latest > current);
    }
}

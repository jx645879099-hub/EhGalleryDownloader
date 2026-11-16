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
        var handler = NetworkRouting.CreateHandler(proxyUrl);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EhGalleryDownloader/1.1");
        return await CheckUsingClientAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ApplicationUpdateInfo> CheckUsingClientAsync(
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(
            LatestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var tag = GitHubReleaseParser.GetTag(response);
        if (tag.Length == 0)
            throw new InvalidOperationException("GitHub 没有返回可识别的软件版本。请稍后再试。");
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest))
            throw new InvalidOperationException($"无法识别远程版本号：{tag}");
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build), Math.Max(0, latest.Revision));
        return new ApplicationUpdateInfo(
            current,
            latest,
            tag,
            $"https://github.com/jx645879099-hub/EhGalleryDownloader/releases/tag/{Uri.EscapeDataString(tag)}",
            latest > current);
    }
}

public static class GitHubReleaseParser
{
    public static string GetTag(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? response.RequestMessage?.RequestUri;
        if (location is null) return "";
        if (!location.IsAbsoluteUri)
        {
            if (response.RequestMessage?.RequestUri is not { } origin) return "";
            location = new Uri(origin, location);
        }
        if (location.Scheme != "https"
            || !location.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return "";
        const string marker = "/releases/tag/";
        var index = location.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "" : Uri.UnescapeDataString(location.AbsolutePath[(index + marker.Length)..].Trim('/'));
    }
}

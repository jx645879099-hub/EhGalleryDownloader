using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace EhGalleryDownloader;

public static class NetworkRouting
{
    public static string NormalizeProxy(string? value)
    {
        var text = value?.Trim() ?? "";
        return text.Length > 0 && !text.Contains("://") ? "http://" + text : text;
    }

    public static bool TryGetProxyUri(string? value, out Uri? uri) =>
        Uri.TryCreate(NormalizeProxy(value), UriKind.Absolute, out uri)
        && uri.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5"
        && !string.IsNullOrWhiteSpace(uri.Host);

    public static HttpClientHandler CreateHandler(string? proxyUrl, bool allowAutoRedirect = true)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            UseProxy = !string.IsNullOrWhiteSpace(proxyUrl)
        };
        if (handler.UseProxy)
        {
            if (!TryGetProxyUri(proxyUrl, out var uri))
            {
                handler.Dispose();
                throw new ArgumentException("代理地址无效。请填写 HTTP / SOCKS 代理地址，或切换为“直连”。");
            }
            handler.Proxy = new WebProxy(uri!);
        }
        return handler;
    }

    public static void ConfigureGalleryDl(ProcessStartInfo info, bool useProxy, string? proxyUrl)
    {
        // requests otherwise reads environment and Windows proxy settings even when
        // no --proxy argument is supplied. Both routes must follow our selection.
        info.ArgumentList.Add("--option");
        info.ArgumentList.Add("proxy-env=false");
        foreach (var key in info.Environment.Keys.Where(key =>
                     key.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        if (!useProxy) return;
        if (!TryGetProxyUri(proxyUrl, out _))
            throw new ArgumentException("代理地址无效。请检查网络设置，或切换为“直连”。");
        info.ArgumentList.Add("--proxy");
        info.ArgumentList.Add(NormalizeProxy(proxyUrl));
    }
}

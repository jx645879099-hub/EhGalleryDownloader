using System.Net;
using System.Net.Http;

namespace EhGalleryDownloader;

public sealed record CookieVerificationResult(
    bool EhentaiValid,
    bool ExhentaiValid,
    string Message)
{
    public bool CanAccess(string galleryUrl) =>
        GalleryUrlValidator.TryNormalize(galleryUrl, out var normalized, out _)
        && (new Uri(normalized).Host.EndsWith("exhentai.org", StringComparison.OrdinalIgnoreCase)
            ? ExhentaiValid : EhentaiValid);
}

public static class CookieVerificationService
{
    public static async Task<CookieVerificationResult> VerifyAsync(
        string cookieText,
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        if (!CookieParser.TryParse(cookieText, out _, out var parseError))
            return new CookieVerificationResult(false, false, parseError);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            UseProxy = !string.IsNullOrWhiteSpace(proxyUrl)
        };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        return await VerifyAsync(cookieText, client, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CookieVerificationResult> VerifyAsync(
        string cookieText,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        if (!CookieParser.TryParse(cookieText, out var cookies, out var parseError))
            return new CookieVerificationResult(false, false, parseError);

        var cookieHeader = CookieParser.ToRequestHeader(cookies);

        using var ehResponse = await SendAsync(
            client, "https://e-hentai.org/home.php", cookieHeader, cancellationToken)
            .ConfigureAwait(false);
        var ehBody = await ehResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var ehValid = ehResponse.IsSuccessStatusCode
                      && !ContainsLoginFailure(ehBody)
                      && !(ehResponse.Headers.Location?.ToString().Contains(
                          "bounce_login", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!ehValid)
        {
            return new CookieVerificationResult(false, false,
                "Cookie 格式可以识别，但 E-Hentai 没有接受该登录状态。请重新复制当前页面请求中的完整 Cookie。");
        }

        var exValid = false;
        try
        {
            using var exResponse = await SendAsync(
                client, "https://exhentai.org/", cookieHeader, cancellationToken)
                .ConfigureAwait(false);
            var exBody = await exResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var igneousLooksValid = cookies.TryGetValue("igneous", out var igneous)
                                    && !string.IsNullOrWhiteSpace(igneous)
                                    && !igneous.Equals("mystery", StringComparison.OrdinalIgnoreCase)
                                    && !igneous.Equals("null", StringComparison.OrdinalIgnoreCase);
            exValid = exResponse.IsSuccessStatusCode
                      && igneousLooksValid
                      && exBody.Length > 500
                      && !exBody.Contains("Sad Panda", StringComparison.OrdinalIgnoreCase)
                      && !ContainsLoginFailure(exBody);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // E-Hentai 已验证时，ExHentai 的单独网络失败不应抹掉前者结果。
        }

        return exValid
            ? new CookieVerificationResult(true, true, "登录有效：E-Hentai 和 ExHentai 均可使用。")
            : new CookieVerificationResult(true, false,
                "E-Hentai 登录有效，但 ExHentai 未通过验证。下载 ExHentai 画廊前，"
                + "请先在浏览器确认可以打开该画廊，再从 ExHentai 页面重新导入 Cookie。"
                + "如果浏览器也无法打开，请先检查账号访问权限。");
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string url, string cookieHeader, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ContainsLoginFailure(string body) =>
        body.Contains("This page requires you to log on", StringComparison.OrdinalIgnoreCase)
        || body.Contains("bounce_login", StringComparison.OrdinalIgnoreCase);
}

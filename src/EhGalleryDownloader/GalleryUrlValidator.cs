namespace EhGalleryDownloader;

public static class GalleryUrlValidator
{
    private static readonly HashSet<string> SupportedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "e-hentai.org",
        "www.e-hentai.org",
        "exhentai.org",
        "www.exhentai.org"
    };

    public static IReadOnlyList<string> ParseMany(string text, out string error)
    {
        var values = text
            .Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (values.Count == 0)
        {
            error = "请至少粘贴一条画廊链接。";
            return [];
        }

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (!TryNormalize(value, out var normalized, out error))
                return [];
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }
        error = "";
        return result;
    }

    public static bool TryNormalize(string value, out string normalized, out string error)
    {
        normalized = "";
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "画廊链接必须是完整的 https:// 地址。";
            return false;
        }
        if (!SupportedHosts.Contains(uri.Host))
        {
            error = $"不支持的网站：{uri.Host}。请使用 E-Hentai 或 ExHentai 画廊首页链接。";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3
            || !segments[0].Equals("g", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(segments[1], out _)
            || segments[2].Length < 5)
        {
            error = "链接不是有效的画廊首页。正确格式类似：https://e-hentai.org/g/数字/token/";
            return false;
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host)
        {
            Path = $"/g/{segments[1]}/{segments[2]}/",
            Query = "",
            Fragment = ""
        };
        normalized = builder.Uri.AbsoluteUri;
        error = "";
        return true;
    }
}

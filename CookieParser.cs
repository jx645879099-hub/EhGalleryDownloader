namespace EhGalleryDownloader;

public static class CookieParser
{
    public static bool TryParse(string? input, out Dictionary<string, string> cookies, out string error)
    {
        cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Cookie 为空。";
            return false;
        }

        var raw = input.Trim();
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var cookieLine = lines.FirstOrDefault(line =>
            line.TrimStart().StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase));
        if (cookieLine is not null)
            raw = cookieLine[(cookieLine.IndexOf(':') + 1)..].Trim();
        else if (raw.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            raw = raw[7..].Trim();

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var name = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace)
                                 || name.Any(char.IsControl)
                                 || name.Contains(':'))
                continue;
            cookies[name] = value;
        }

        if (cookies.Count == 0)
        {
            error = "没有识别到“名称=值”的 Cookie。请复制 Request Headers 中 Cookie: 后面的完整内容。";
            return false;
        }
        return true;
    }

    public static string ToRequestHeader(IReadOnlyDictionary<string, string> cookies) =>
        string.Join("; ", cookies.Select(pair => $"{pair.Key}={pair.Value}"));
}

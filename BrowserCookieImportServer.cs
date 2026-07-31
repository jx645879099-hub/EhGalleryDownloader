using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EhGalleryDownloader;

public sealed record BrowserCookieItem(string Name, string Value, string Domain);

public sealed record BrowserCookieImportPayload(
    string Token,
    string Browser,
    string PageUrl,
    IReadOnlyList<BrowserCookieItem> Cookies);

public sealed record BrowserCookieImportResult(
    string CookieHeader,
    string Browser,
    string PageUrl);

public sealed class BrowserCookieImportServer : IAsyncDisposable
{
    public const int FirstPort = 17893;
    public const int LastPort = 17902;
    private const int MaxRequestBytes = 32 * 1024;
    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private bool _disposed;

    public int Port { get; }

    private BrowserCookieImportServer(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public static BrowserCookieImportServer Start()
    {
        for (var port = FirstPort; port <= LastPort; port++)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(4);
                return new BrowserCookieImportServer(listener, port);
            }
            catch (SocketException)
            {
                listener?.Stop();
            }
        }

        throw new InvalidOperationException(
            "无法启动浏览器导入通道。请关闭另一个画廊下载助手窗口后重试。");
    }

    public async Task<BrowserCookieImportResult> WaitForImportAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCancellation.Token);

        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(linked.Token)
                    .ConfigureAwait(false);
                var result = await HandleClientAsync(client, linked.Token).ConfigureAwait(false);
                if (result is not null) return result;
            }
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待浏览器响应超时，请重新点击导入后再试。");
        }
    }

    public static bool TryCreateCookieHeader(
        BrowserCookieImportPayload? payload,
        out string cookieHeader,
        out string error)
    {
        cookieHeader = "";
        error = "";
        if (payload?.Cookies is null || payload.Cookies.Count == 0)
        {
            error = "当前网页没有找到可用的登录 Cookie。请确认网页已经登录。";
            return false;
        }

        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ipb_member_id", "ipb_pass_hash", "igneous"
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cookie in payload.Cookies)
        {
            if (!allowedNames.Contains(cookie.Name)
                || !IsAllowedDomain(cookie.Domain)
                || string.IsNullOrWhiteSpace(cookie.Value)
                || cookie.Value.Length > 4096
                || cookie.Value.Any(ch => ch is '\r' or '\n' or ';'))
                continue;
            values[cookie.Name] = cookie.Value;
        }

        if (!values.ContainsKey("ipb_member_id") || !values.ContainsKey("ipb_pass_hash"))
        {
            error = "没有找到完整的登录状态。请在已登录的 E-Hentai 或 ExHentai 页面重试。";
            return false;
        }

        cookieHeader = CookieParser.ToRequestHeader(values);
        return true;
    }

    private async Task<BrowserCookieImportResult?> HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        await using var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request is null) return null;

        var originAllowed = request.Origin.StartsWith(
            "chrome-extension://", StringComparison.OrdinalIgnoreCase);
        if (request.Method == "OPTIONS")
        {
            await WriteResponseAsync(stream, originAllowed ? 204 : 403, "", request.Origin)
                .ConfigureAwait(false);
            return null;
        }

        if (!originAllowed || request.ExtensionMarker != "1")
        {
            await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden\"}", "")
                .ConfigureAwait(false);
            return null;
        }

        if (request.Method == "GET" && request.Path == "/session")
        {
            var json = JsonSerializer.Serialize(new
            {
                token = _token,
                app = "EhGalleryDownloader",
                expiresInSeconds = 120
            });
            await WriteResponseAsync(stream, 200, json, request.Origin).ConfigureAwait(false);
            return null;
        }

        if (request.Method != "POST" || request.Path != "/import")
        {
            await WriteResponseAsync(stream, 404, "{\"error\":\"not_found\"}", request.Origin)
                .ConfigureAwait(false);
            return null;
        }

        BrowserCookieImportPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BrowserCookieImportPayload>(
                request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            await WriteResponseAsync(stream, 400, "{\"error\":\"invalid_json\"}", request.Origin)
                .ConfigureAwait(false);
            return null;
        }

        if (payload is null || !CryptographicEquals(payload.Token, _token))
        {
            await WriteResponseAsync(stream, 403, "{\"error\":\"invalid_token\"}", request.Origin)
                .ConfigureAwait(false);
            return null;
        }

        if (!TryCreateCookieHeader(payload, out var header, out var error))
        {
            await WriteResponseAsync(
                    stream, 422, JsonSerializer.Serialize(new { error }), request.Origin)
                .ConfigureAwait(false);
            return null;
        }

        await WriteResponseAsync(stream, 200, "{\"ok\":true}", request.Origin)
            .ConfigureAwait(false);
        return new BrowserCookieImportResult(header, payload.Browser, payload.PageUrl);
    }

    private static bool IsAllowedDomain(string domain)
    {
        var normalized = domain.Trim().TrimStart('.');
        return normalized.Equals("e-hentai.org", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(".e-hentai.org", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("exhentai.org", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(".exhentai.org", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? "");
        var rightBytes = Encoding.UTF8.GetBytes(right ?? "");
        return leftBytes.Length == rightBytes.Length
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   leftBytes, rightBytes);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[2048];
        var headerEnd = -1;
        while (memory.Length < MaxRequestBytes && headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            memory.Write(buffer, 0, read);
            headerEnd = FindHeaderEnd(memory.GetBuffer(), (int)memory.Length);
        }
        if (headerEnd < 0) return null;

        var bytes = memory.ToArray();
        var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;

        var headers = lines.Skip(1)
            .Select(line => (Index: line.IndexOf(':'), Line: line))
            .Where(item => item.Index > 0)
            .ToDictionary(
                item => item.Line[..item.Index].Trim(),
                item => item.Line[(item.Index + 1)..].Trim(),
                StringComparer.OrdinalIgnoreCase);
        var contentLength = headers.TryGetValue("Content-Length", out var contentLengthText)
                            && int.TryParse(contentLengthText, out var parsedLength)
            ? parsedLength
            : 0;
        if (contentLength < 0 || contentLength > MaxRequestBytes) return null;

        var bodyOffset = headerEnd + 4;
        while (bytes.Length - bodyOffset < contentLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            if (memory.Length > MaxRequestBytes) return null;
            bytes = memory.ToArray();
        }

        var body = contentLength == 0
            ? ""
            : Encoding.UTF8.GetString(bytes, bodyOffset, Math.Min(contentLength, bytes.Length - bodyOffset));
        return new HttpRequest(
            requestLine[0].ToUpperInvariant(),
            requestLine[1].Split('?', 2)[0],
            headers.GetValueOrDefault("Origin") ?? "",
            headers.GetValueOrDefault("X-EhGallery-Extension") ?? "",
            body);
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (var index = 3; index < length; index++)
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n'
                && bytes[index - 1] == '\r' && bytes[index] == '\n')
                return index - 3;
        return -1;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string body,
        string allowedOrigin)
    {
        var status = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            422 => "Unprocessable Content",
            _ => "Error"
        };
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {statusCode} {status}\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append($"Content-Length: {bodyBytes.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (!string.IsNullOrWhiteSpace(allowedOrigin))
        {
            headers.Append($"Access-Control-Allow-Origin: {allowedOrigin}\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n")
                .Append("Access-Control-Allow-Headers: Content-Type, X-EhGallery-Extension\r\n")
                .Append("Vary: Origin\r\n");
        }
        headers.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _listener.Stop();
        }
        return ValueTask.CompletedTask;
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        string Origin,
        string ExtensionMarker,
        string Body);
}

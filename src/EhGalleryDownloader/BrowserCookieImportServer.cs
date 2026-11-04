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
    public static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(5);
    private const int MaxRequestBytes = 32 * 1024;
    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private bool _disposed;
    private DateTimeOffset _expiresAt;

    public int Port { get; }
    public event Action<string>? StatusChanged;

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
        _expiresAt = DateTimeOffset.UtcNow.Add(timeout);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCancellation.Token);
        var completion = new TaskCompletionSource<BrowserCookieImportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new List<Task>();

        async Task HandleConnectionAsync(TcpClient client)
        {
            using (client)
            using (var clientDeadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                clientDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var result = await HandleClientAsync(client, clientDeadline.Token).ConfigureAwait(false);
                    if (result is not null) completion.TrySetResult(result);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (SocketException) { }
                catch (Exception ex) { completion.TrySetException(ex); }
            }
        }

        async Task AcceptConnectionsAsync()
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                clients.RemoveAll(task => task.IsCompleted);
                if (clients.Count >= 4) { client.Dispose(); continue; }
                clients.Add(HandleConnectionAsync(client));
            }
        }

        var accepting = AcceptConnectionsAsync();
        try
        {
            if (await Task.WhenAny(completion.Task, accepting).ConfigureAwait(false) == accepting)
                await accepting.ConfigureAwait(false);
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待已结束，尚未收到登录信息。请确认 Chrome 已安装“画廊登录导入”扩展，"
                + "然后重新开始接收，并在已登录的画廊网页点击扩展里的“发送到下载助手”。");
        }
        finally
        {
            linked.Cancel();
            try { await accepting.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    public static bool TryCreateCookieHeader(
        BrowserCookieImportPayload? payload,
        out string cookieHeader,
        out string error)
    {
        cookieHeader = "";
        error = "";
        if (!Uri.TryCreate(payload?.PageUrl, UriKind.Absolute, out var pageUri)
            || !CookieParser.CanSendTo(pageUri))
        {
            error = "请在已登录的 HTTPS 画廊网页打开扩展，不能从其他网站导入。";
            return false;
        }
        if (payload?.Cookies is null || payload.Cookies.Count == 0)
        {
            error = "当前网页没有找到可用的登录 Cookie。请确认网页已经登录。";
            return false;
        }

        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ipb_member_id", "ipb_pass_hash", "igneous"
        };
        var ehValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cookie in payload.Cookies)
        {
            if (cookie is null || string.IsNullOrWhiteSpace(cookie.Name)
                || string.IsNullOrWhiteSpace(cookie.Domain)
                || !allowedNames.Contains(cookie.Name)
                || !IsAllowedDomain(cookie.Domain)
                || string.IsNullOrWhiteSpace(cookie.Value)
                || cookie.Value.Length > 4096
                || cookie.Value.Any(ch => ch is '\r' or '\n' or ';'))
                continue;
            var siteValues = IsExhentaiDomain(cookie.Domain) ? exValues : ehValues;
            // chrome.cookies.getAll returns the most specific applicable path first.
            siteValues.TryAdd(cookie.Name, cookie.Value);
        }

        var isExhentai = IsExhentaiDomain(pageUri.Host);
        var preferred = isExhentai ? exValues : ehValues;
        var fallback = isExhentai ? ehValues : exValues;
        var account = HasAccountPair(preferred) ? preferred : fallback;
        if (!HasAccountPair(account))
        {
            error = "没有找到同一网站的完整登录信息。请在当前 Chrome 用户的画廊网页重新登录，再打开扩展发送。";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ipb_member_id"] = account["ipb_member_id"],
            ["ipb_pass_hash"] = account["ipb_pass_hash"]
        };
        var hasIgneous = exValues.TryGetValue("igneous", out var igneous)
            && !igneous.Equals("mystery", StringComparison.OrdinalIgnoreCase)
            && !igneous.Equals("null", StringComparison.OrdinalIgnoreCase);
        if (isExhentai && !hasIgneous)
        {
            error = "已找到账号登录信息，但缺少 ExHentai 的有效 igneous。请先在同一 Chrome 用户中打开并刷新 ExHentai，"
                + "确认能看到画廊内容后，再点击扩展发送。如果仍是空白页，请检查账号访问权限。";
            return false;
        }
        if (hasIgneous) values["igneous"] = igneous!;
        cookieHeader = CookieParser.ToRequestHeader(values);
        return true;
    }

    private static bool HasAccountPair(IReadOnlyDictionary<string, string> values) =>
        values.ContainsKey("ipb_member_id") && values.ContainsKey("ipb_pass_hash");

    private static bool IsExhentaiDomain(string domain) =>
        domain.TrimStart('.').Equals("exhentai.org", StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith(".exhentai.org", StringComparison.OrdinalIgnoreCase);

    private async Task<BrowserCookieImportResult?> HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request is null) return null;

        var extensionOrigin = request.Origin.StartsWith(
            "chrome-extension://", StringComparison.OrdinalIgnoreCase);
        // Host-permitted Chrome / Edge extension fetches can omit Origin.
        // Keep the non-safelisted marker and import token mandatory, reject supplied
        // website origins, and validate Host so DNS rebinding cannot use this path.
        var originAllowed = request.Origin.Length == 0 || extensionOrigin;
        if (!request.Host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || !originAllowed)
        {
            await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden\"}", "", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        if (request.Method == "OPTIONS")
        {
            await WriteResponseAsync(stream, extensionOrigin ? 204 : 403, "",
                    extensionOrigin ? request.Origin : "", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (request.ExtensionMarker != "1")
        {
            await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden\"}", "", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (request.Method == "GET" && request.Path == "/session")
        {
            StatusChanged?.Invoke("浏览器扩展已连接，正在等待它发送登录信息");
            var json = JsonSerializer.Serialize(new
            {
                token = _token,
                app = "EhGalleryDownloader",
                expiresInSeconds = Math.Max(0, (int)(_expiresAt - DateTimeOffset.UtcNow).TotalSeconds)
            });
            await WriteResponseAsync(stream, 200, json, request.Origin, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (request.Method != "POST" || request.Path != "/import")
        {
            await WriteResponseAsync(stream, 404, "{\"error\":\"not_found\"}", request.Origin, cancellationToken)
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
            await WriteResponseAsync(stream, 400, "{\"error\":\"invalid_json\"}", request.Origin, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (payload is null || !CryptographicEquals(payload.Token, _token))
        {
            await WriteResponseAsync(stream, 403, "{\"error\":\"invalid_token\"}", request.Origin, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (!TryCreateCookieHeader(payload, out var header, out var error))
        {
            StatusChanged?.Invoke("扩展已连接，但登录信息未通过检查：" + error);
            await WriteResponseAsync(
                    stream, 422, JsonSerializer.Serialize(new { error }), request.Origin, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        await WriteResponseAsync(stream, 200, "{\"ok\":true}", request.Origin, cancellationToken)
            .ConfigureAwait(false);
        StatusChanged?.Invoke("已收到浏览器登录信息，接下来验证网站是否接受");
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

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var index = line.IndexOf(':');
            if (index <= 0 || !headers.TryAdd(line[..index].Trim(), line[(index + 1)..].Trim()))
                return null;
        }
        var contentLength = 0;
        if (headers.ContainsKey("Transfer-Encoding")
            || (headers.TryGetValue("Content-Length", out var contentLengthText)
                && !int.TryParse(contentLengthText, out contentLength))
            || contentLength < 0 || headerEnd + 4 + contentLength > MaxRequestBytes) return null;

        var bodyOffset = headerEnd + 4;
        while (bytes.Length - bodyOffset < contentLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
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
            headers.GetValueOrDefault("Host") ?? "",
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
        string allowedOrigin,
        CancellationToken cancellationToken)
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
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
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
        string Host,
        string Origin,
        string ExtensionMarker,
        string Body);
}

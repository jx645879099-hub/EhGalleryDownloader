using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EhGalleryDownloader;

public sealed class MihomoClient : IDisposable
{
    private readonly HttpClient _http;

    public MihomoClient(string endpoint, string secret = "")
    {
        Endpoint = NormalizeEndpoint(endpoint);
        HttpMessageHandler handler;
        Uri baseAddress;

        if (Endpoint.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase))
        {
            var pipeName = Endpoint["pipe://".Length..].Trim('/');
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("命名管道名称不能为空。", nameof(endpoint));

            handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var pipe = new NamedPipeClientStream(
                        ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(cancellationToken);
                        return pipe;
                    }
                    catch
                    {
                        pipe.Dispose();
                        throw;
                    }
                }
            };
            baseAddress = new Uri("http://localhost/");
        }
        else
        {
            handler = new HttpClientHandler { UseProxy = false };
            baseAddress = new Uri(Endpoint);
        }

        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(8)
        };
        if (!string.IsNullOrWhiteSpace(secret))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", secret.Trim());
    }

    public string Endpoint { get; }

    public async Task<ClashState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        using var proxiesDoc = await GetJsonAsync("proxies", cancellationToken);
        using var configDoc = await GetJsonAsync("configs", cancellationToken);
        var proxies = new Dictionary<string, ProxyInfo>(StringComparer.Ordinal);

        if (proxiesDoc.RootElement.TryGetProperty("proxies", out var proxiesElement))
        {
            foreach (var property in proxiesElement.EnumerateObject())
            {
                var value = property.Value;
                var all = new List<string>();
                if (value.TryGetProperty("all", out var allElement)
                    && allElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in allElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                            all.Add(name);
                    }
                }

                proxies[property.Name] = new ProxyInfo
                {
                    Name = property.Name,
                    Type = ReadString(value, "type"),
                    Now = ReadString(value, "now"),
                    All = all
                };
            }
        }

        var config = configDoc.RootElement;
        var mixedPort = ReadInt(config, "mixed-port");
        var httpPort = ReadInt(config, "port");
        var socksPort = ReadInt(config, "socks-port");
        var proxyUrl = "";
        var proxyPortKind = "";
        var effectivePort = 0;
        if (mixedPort > 0)
        {
            effectivePort = mixedPort;
            proxyUrl = $"http://127.0.0.1:{mixedPort}";
            proxyPortKind = "mixed-port";
        }
        else if (httpPort > 0)
        {
            effectivePort = httpPort;
            proxyUrl = $"http://127.0.0.1:{httpPort}";
            proxyPortKind = "port";
        }
        else if (socksPort > 0)
        {
            effectivePort = socksPort;
            proxyUrl = $"socks5://127.0.0.1:{socksPort}";
            proxyPortKind = "socks-port";
        }

        return new ClashState
        {
            Proxies = proxies,
            MixedPort = effectivePort,
            ProxyUrl = proxyUrl,
            ProxyPortKind = proxyPortKind,
            Mode = ReadString(config, "mode")
        };
    }

    public async Task<IReadOnlyDictionary<string, int>> MeasureGroupDelaysAsync(
        string groupName,
        IReadOnlyCollection<string> nodeNames,
        Uri testUrl,
        int timeoutMilliseconds,
        string expectedStatus = "200-399",
        CancellationToken cancellationToken = default)
        => await RefreshAndMeasureGroupDelaysAsync(
            groupName,
            nodeNames,
            testUrl,
            timeoutMilliseconds,
            expectedStatus,
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, int>> RefreshAndMeasureGroupDelaysAsync(
        string groupName,
        IReadOnlyCollection<string> nodeNames,
        Uri testUrl,
        int timeoutMilliseconds,
        string expectedStatus = "200-399",
        CancellationToken cancellationToken = default)
    {
        if (nodeNames.Count == 0)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            var groupDelays = await GetGroupDelaysAsync(
                groupName,
                testUrl,
                timeoutMilliseconds,
                expectedStatus,
                cancellationToken);
            if (groupDelays.Count > 0)
            {
                return nodeNames.ToDictionary(
                    name => name,
                    name => groupDelays.TryGetValue(name, out var delay) ? delay : 0,
                    StringComparer.Ordinal);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Older controller builds may not expose the group endpoint.
            // Fall back to bounded parallel single-node tests.
        }

        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(10);
        var sync = new object();
        var tasks = nodeNames.Select(async nodeName =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var delay = await GetProxyDelayAsync(
                    nodeName,
                    testUrl,
                    timeoutMilliseconds,
                    expectedStatus,
                    cancellationToken);
                lock (sync) results[nodeName] = delay;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                lock (sync) results[nodeName] = 0;
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    public async Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(
            $"proxies/{Uri.EscapeDataString(groupName)}",
            new { name = proxyName },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PatchAsJsonAsync(
            "configs",
            new { mode = mode.Trim().ToLowerInvariant() },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> HasConnectionThroughNodeAsync(
        string host,
        string nodeName,
        CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync("connections", cancellationToken);
        if (!doc.RootElement.TryGetProperty("connections", out var connections)
            || connections.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var connection in connections.EnumerateArray())
        {
            if (!connection.TryGetProperty("metadata", out var metadata)
                || !ReadString(metadata, "host").Equals(host, StringComparison.OrdinalIgnoreCase)
                || !connection.TryGetProperty("chains", out var chains)
                || chains.ValueKind != JsonValueKind.Array)
                continue;

            if (chains.EnumerateArray().Any(chain =>
                    chain.ValueKind == JsonValueKind.String
                    && string.Equals(chain.GetString(), nodeName, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private async Task<IReadOnlyDictionary<string, int>> GetGroupDelaysAsync(
        string groupName,
        Uri testUrl,
        int timeoutMilliseconds,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var path = BuildDelayPath(
            $"group/{Uri.EscapeDataString(groupName)}/delay",
            testUrl,
            timeoutMilliseconds,
            expectedStatus);
        using var doc = await GetJsonAsync(path, cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var delay = ReadDelay(property.Value);
            if (delay > 0 && delay < timeoutMilliseconds)
                result[property.Name] = delay;
        }
        return result;
    }

    private async Task<int> GetProxyDelayAsync(
        string proxyName,
        Uri testUrl,
        int timeoutMilliseconds,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var path = BuildDelayPath(
            $"proxies/{Uri.EscapeDataString(proxyName)}/delay",
            testUrl,
            timeoutMilliseconds,
            expectedStatus);
        using var doc = await GetJsonAsync(path, cancellationToken);
        var delay = ReadDelay(doc.RootElement);
        return delay > 0 && delay < timeoutMilliseconds ? delay : 0;
    }

    private static string BuildDelayPath(
        string path,
        Uri testUrl,
        int timeoutMilliseconds,
        string expectedStatus)
    {
        var query =
            $"url={Uri.EscapeDataString(testUrl.AbsoluteUri)}"
            + $"&timeout={Math.Clamp(timeoutMilliseconds, 500, 15000)}";
        if (!string.IsNullOrWhiteSpace(expectedStatus))
            query += $"&expected={Uri.EscapeDataString(expectedStatus)}";
        query += $"&fresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        return path + "?" + query;
    }

    private static int ReadDelay(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var direct))
            return direct;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var delay = ReadInt(element, "delay");
            if (delay <= 0) delay = ReadInt(element, "meanDelay");
            return delay;
        }
        return 0;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        request.Headers.Pragma.ParseAdd("no-cache");
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = "";
        try { body = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "Clash 控制接口要求密钥，目前的内嵌版本尚未填写控制密钥。");

        throw new HttpRequestException(
            $"Mihomo 控制接口返回 {(int)response.StatusCode} {response.ReasonPhrase}"
            + (string.IsNullOrWhiteSpace(body) ? "" : $"：{body}"));
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var value = endpoint.Trim();
        if (string.IsNullOrEmpty(value)) value = "pipe://verge-mihomo";
        if (value.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase))
            return "pipe://" + value["pipe://".Length..].Trim('/');
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "http://" + value;
        return value.TrimEnd('/') + "/";
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && property.TryGetInt32(out var value)
            ? value
            : 0;

    public void Dispose() => _http.Dispose();
}

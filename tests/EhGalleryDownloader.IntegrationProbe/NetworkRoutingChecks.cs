using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EhGalleryDownloader;

internal static class NetworkRoutingChecks
{
    public static async Task RunAsync()
    {
        Require(!new AppSettings().UseProxy, "New installations must default to direct connections.");
        using (var direct = NetworkRouting.CreateHandler(null))
            Require(!direct.UseProxy, "Direct HTTP traffic must ignore Windows and environment proxies.");
        Require(NetworkRouting.TryGetProxyUri("127.0.0.1:7897", out var parsed)
                && parsed?.Scheme == "http"
                && !NetworkRouting.TryGetProxyUri("file:///tmp/proxy", out _),
            "Proxy address validation is inconsistent.");

        var startInfo = new ProcessStartInfo();
        startInfo.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        startInfo.Environment["https_proxy"] = "http://127.0.0.1:1";
        startInfo.Environment["ALL_PROXY"] = "socks5://127.0.0.1:1";
        NetworkRouting.ConfigureGalleryDl(startInfo, false, "invalid saved proxy");
        Require(startInfo.ArgumentList.Contains("proxy-env=false")
                && !startInfo.ArgumentList.Contains("--proxy")
                && !startInfo.Environment.Keys.Any(key => key.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase)),
            "Direct gallery-dl processes retain an implicit proxy.");
        Console.WriteLine("PASS: direct mode ignores stale proxy addresses and inherited proxy environment variables.");

        // Exercise the actual HTTP transport against an origin and a recording
        // proxy. The proxy target cannot resolve and must be routed explicitly.
        var directLine = await SendThroughLoopbackAsync(useProxy: false);
        var proxyLine = await SendThroughLoopbackAsync(useProxy: true);
        Require(directLine.StartsWith("GET /route-check ", StringComparison.Ordinal)
                && proxyLine.StartsWith("GET http://route-check.invalid/route-check ", StringComparison.Ordinal),
            "HTTP direct/proxy requests did not take their selected route.");
        Console.WriteLine("PASS: real HTTP requests use the chosen origin or explicit proxy.");

        var options = new DownloadOptions("none", "edge", false, null, false, "bad unused address",
            true, false, false, true);
        foreach (var proxy in new[] { false, true })
        {
            var samples = await GallerySampleService.GetSampleUrlsAsync(
                Environment.ProcessPath!, "https://e-hentai.org/g/99999/network-fixture/",
                options with { UseProxy = proxy, ProxyUrl = proxy ? "127.0.0.1:1" : "bad unused address" },
                CancellationToken.None);
            Require(samples.Count == 1, "Sample extraction lost the selected network route.");
        }
        Console.WriteLine("PASS: sample-extraction child processes support direct and explicit proxy modes.");

        using (var updateClient = new HttpClient(new FixtureHandler((request, token) =>
                   Task.FromResult(Response(HttpStatusCode.OK,
                       "https://github.com/jx645879099-hub/EhGalleryDownloader/releases/tag/v9.2.1?source=test")))))
        {
            var info = await ApplicationUpdateService.CheckUsingClientAsync(updateClient);
            Require(info.Tag == "v9.2.1" && info.IsUpdateAvailable && info.LatestVersion == new Version(9, 2, 1, 0),
                "Software update parsing lost the final redirect target or version.");
        }
        using (var limitedClient = new HttpClient(new FixtureHandler((request, token) =>
                   Task.FromResult(Response(HttpStatusCode.TooManyRequests, request.RequestUri!.ToString())))))
        {
            try
            {
                await ApplicationUpdateService.CheckUsingClientAsync(limitedClient);
                throw new InvalidOperationException("Rate limiting was reported as valid update metadata.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests) { }
        }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        using (var pendingClient = new HttpClient(new FixtureHandler(async (request, token) =>
               {
                   await Task.Delay(Timeout.InfiniteTimeSpan, token);
                   return Response(HttpStatusCode.OK, request.RequestUri!.ToString());
               })))
        {
            try
            {
                await ApplicationUpdateService.CheckUsingClientAsync(pendingClient, deadline.Token);
                throw new InvalidOperationException("Software update cancellation was ignored.");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
        Console.WriteLine("PASS: software updates handle final redirects, HTTP failures, and cancellation.");

        var root = Path.Combine(Path.GetTempPath(), "eh-network-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckEngineFailuresAsync(root);
            const string digest = "a468359545129a1268ff3e2a59d2b453ff18a7306025a332a834f72eb328105e";
            var paddedAsset = ReleaseAssetParser.Parse(
                "<a href=\"/gdl-org/builds/releases/download/test/gallery-dl_windows.exe\">x64</a>"
                + new string(' ', 1800) + $"<span>sha256:{digest}</span>", "test");
            var unrelatedDigest = ReleaseAssetParser.Parse(
                "<a href=\"/gdl-org/builds/releases/download/test/gallery-dl_windows.exe\">x64</a>"
                + "<a href=\"/gdl-org/builds/releases/download/test/source.tar.gz\">source</a>"
                + $"<span>sha256:{digest}</span>", "test");
            Require(paddedAsset.Sha256Digest == digest && unrelatedDigest.Sha256Digest is null,
                "Update checksum lookup is truncated or mixed with another asset.");

            var manager = new EngineManager((proxy, redirects, timeout) =>
                new HttpClient(new FixtureHandler((request, token) => Task.FromResult(
                    request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
                        ? Response(HttpStatusCode.OK, "https://github.com/gdl-org/builds/releases/tag/test")
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "<a href=\"/gdl-org/builds/releases/download/test/gallery-dl_windows.exe\">x64</a>"
                                + $"<span>sha256:{digest}</span>")
                        }))), root);
            var info = await manager.CheckForUpdateAsync(null);
            Require(info.RemoteVersion == "test" && info.Sha256Digest == digest,
                "Engine update lookup failed after redirect resolution.");

            var installedEngine = manager.BundledEnginePath;
            Directory.CreateDirectory(Path.GetDirectoryName(installedEngine)!);
            var original = Encoding.UTF8.GetBytes("existing-engine-must-survive-a-bad-update");
            await File.WriteAllBytesAsync(installedEngine, original);
            var badManager = new EngineManager((proxy, redirects, timeout) =>
                new HttpClient(new FixtureHandler((request, token) => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[1024 * 1024 + 1])
                    }))), root);
            try
            {
                await badManager.InstallAsync(info, null);
                throw new InvalidOperationException("An invalid engine update was accepted.");
            }
            catch (EngineUpdateException ex) when (ex.Kind == EngineUpdateFailureKind.InvalidDownload) { }
            Require((await File.ReadAllBytesAsync(installedEngine)).SequenceEqual(original)
                    && !File.Exists(installedEngine + ".update.exe"),
                "A failed update changed the previous engine or left a partial replacement.");
            Console.WriteLine("PASS: engine checks handle redirects and asset checksums; failed installs preserve the existing component.");
        }
        finally { Directory.Delete(root, recursive: true); }

        Require(!DownloadErrorClassifier.DescribeFailure(1, "connection timeout", 0).Contains("代理")
                && DownloadErrorClassifier.DescribeFailure(1, "connection timeout", 0, true).Contains("代理"),
            "Direct-mode errors still require a proxy.");
        Console.WriteLine("PASS: network error advice matches direct or proxy mode.");
    }

    private static async Task CheckEngineFailuresAsync(string root)
    {
        foreach (var proxy in new string?[] { null, "http://127.0.0.1:1" })
        {
            var manager = new EngineManager((route, redirects, timeout) =>
                new HttpClient(new FixtureHandler((request, token) =>
                    throw new HttpRequestException("Connection refused"))), root);
            try
            {
                await manager.CheckForUpdateAsync(proxy);
                throw new InvalidOperationException("A failed engine connection was reported as successful.");
            }
            catch (EngineUpdateException ex)
            {
                Require(ex.Kind == (proxy is null ? EngineUpdateFailureKind.Unknown : EngineUpdateFailureKind.ProxyUnavailable),
                    "Direct failures are incorrectly classified as proxy failures.");
            }
        }
        var limited = new EngineManager((route, redirects, timeout) =>
            new HttpClient(new FixtureHandler((request, token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)))), root);
        try
        {
            await limited.CheckForUpdateAsync(null);
            throw new InvalidOperationException("Rate-limited engine metadata was accepted.");
        }
        catch (EngineUpdateException ex) when (ex.Kind == EngineUpdateFailureKind.RateLimited) { }
    }

    private static async Task<string> SendThroughLoopbackAsync(bool useProxy)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            async Task<string> ReceiveAsync()
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var firstLine = await reader.ReadLineAsync(deadline.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), deadline.Token);
                return firstLine;
            }
            var receive = ReceiveAsync();
            using var client = new HttpClient(NetworkRouting.CreateHandler(useProxy ? $"http://127.0.0.1:{port}" : null));
            using var response = await client.GetAsync(useProxy
                ? "http://route-check.invalid/route-check"
                : $"http://127.0.0.1:{port}/route-check", deadline.Token);
            response.EnsureSuccessStatusCode();
            return await receive;
        }
        finally { listener.Stop(); }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string finalUrl) =>
        new(status) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl), Content = new StringContent("") };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }
}

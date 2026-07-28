using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace EhGalleryDownloader;

public sealed class GalleryNodeTestService
{
    private readonly MihomoClient _controller;
    private readonly Uri _proxy;

    public GalleryNodeTestService(MihomoClient controller, string proxyUrl)
    {
        _controller = controller;
        _proxy = new Uri(proxyUrl);
    }

    public async Task<NodeProbeMeasurement> TestAsync(
        string groupName,
        string nodeName,
        IReadOnlyList<Uri> sampleUrls,
        long byteLimitPerSample,
        int timeoutSeconds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await _controller.SelectProxyAsync(groupName, nodeName, cancellationToken);
        await Task.Delay(450, cancellationToken);

        var successful = 0;
        var interrupted = 0;
        long totalBytes = 0;
        var totalSeconds = 0d;
        var routeVerified = false;
        var notes = new List<string>();

        for (var index = 0; index < sampleUrls.Count; index++)
        {
            progress?.Report($"样本 {index + 1}/{sampleUrls.Count}");
            var sample = await DownloadSampleAsync(
                sampleUrls[index],
                nodeName,
                byteLimitPerSample,
                timeoutSeconds,
                cancellationToken);

            totalBytes += sample.ReceivedBytes;
            totalSeconds += sample.ElapsedSeconds;
            routeVerified |= sample.RouteVerified;
            if (sample.Success)
                successful++;
            else
                interrupted++;
            if (!string.IsNullOrWhiteSpace(sample.Message))
                notes.Add($"样本 {index + 1}：{sample.Message}");
        }

        var downloadedMb = totalBytes / 1024d / 1024d;
        var speed = downloadedMb / Math.Max(totalSeconds, 0.001);
        notes.Insert(0, routeVerified
            ? "已从 Mihomo 连接表确认测试流量经过此节点。"
            : "未从 Mihomo 连接表确认路径；若所有节点结果异常接近，请改选实际控制下载流量的策略组。");

        return new NodeProbeMeasurement(
            successful,
            sampleUrls.Count,
            interrupted,
            speed,
            downloadedMb,
            string.Join(Environment.NewLine, notes));
    }

    private async Task<SampleMeasurement> DownloadSampleAsync(
        Uri url,
        string nodeName,
        long byteLimit,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(_proxy),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(7, timeoutSeconds)),
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            MaxConnectionsPerServer = 1
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        http.DefaultRequestHeaders.ConnectionClose = true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        long received = 0;
        var timer = new Stopwatch();
        Task<bool>? verification = null;
        using var verifyCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();

            var finalHost = response.RequestMessage?.RequestUri?.Host ?? url.Host;
            verification = VerifyRouteAsync(finalHost, nodeName, verifyCancellation.Token);
            var expected = response.Content.Headers.ContentLength is > 0 and var length
                ? Math.Min(length, byteLimit)
                : byteLimit;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[128 * 1024];
            timer.Start();
            while (received < expected)
            {
                var wanted = (int)Math.Min(buffer.Length, expected - received);
                var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), timeout.Token);
                if (read <= 0) break;
                received += read;
            }
            timer.Stop();
            var verified = await StopVerificationAsync(verification, verifyCancellation);
            var complete = received >= expected
                           || (response.Content.Headers.ContentLength is null && received > 0);
            return new SampleMeasurement(
                complete,
                received,
                Math.Max(timer.Elapsed.TotalSeconds, 0.001),
                verified,
                complete
                    ? $"传输正常，读取 {FormatMb(received)}"
                    : $"图片提前结束，只读取 {FormatMb(received)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            timer.Stop();
            var verified = await StopVerificationAsync(verification, verifyCancellation);
            return new SampleMeasurement(
                false,
                received,
                Math.Max(timer.Elapsed.TotalSeconds, timeoutSeconds),
                verified,
                $"超时，只读取 {FormatMb(received)}");
        }
        catch (Exception ex)
        {
            timer.Stop();
            var verified = await StopVerificationAsync(verification, verifyCancellation);
            return new SampleMeasurement(
                false,
                received,
                Math.Max(timer.Elapsed.TotalSeconds, 0.001),
                verified,
                $"{FriendlyNetworkError(ex)}，只读取 {FormatMb(received)}");
        }
    }

    private async Task<bool> VerifyRouteAsync(
        string host,
        string nodeName,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await _controller.HasConnectionThroughNodeAsync(
                        host, nodeName, cancellationToken))
                    return true;
            }
            catch (OperationCanceledException) { break; }
            catch { }

            try { await Task.Delay(80, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
        return false;
    }

    private static async Task<bool> StopVerificationAsync(
        Task<bool>? verification,
        CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        if (verification is null) return false;
        try { return await verification; }
        catch { return false; }
    }

    private static string FriendlyNetworkError(Exception exception)
    {
        var text = exception.ToString();
        if (text.Contains("UNEXPECTED_EOF", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
            || text.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase))
            return "加密连接中途断开";
        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "连接超时";
        if (text.Contains("proxy", StringComparison.OrdinalIgnoreCase)
            || text.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return "无法连接 Clash 代理";
        return exception.GetBaseException().Message;
    }

    private static string FormatMb(long bytes) => $"{bytes / 1024d / 1024d:F1} MB";

    private sealed record SampleMeasurement(
        bool Success,
        long ReceivedBytes,
        double ElapsedSeconds,
        bool RouteVerified,
        string Message);
}

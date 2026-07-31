using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace EhGalleryDownloader;

public sealed class GalleryNodeTestService
{
    private readonly MihomoClient _controller;
    private readonly Uri _proxy;
    private readonly Uri? _referer;
    private readonly string? _cookieHeader;
    private readonly string? _enginePath;
    private readonly bool _forceIpv4;

    public GalleryNodeTestService(
        MihomoClient controller,
        string proxyUrl,
        string? galleryUrl = null,
        string? cookie = null,
        string? enginePath = null,
        bool forceIpv4 = true)
    {
        _controller = controller;
        _proxy = new Uri(proxyUrl);
        _referer = Uri.TryCreate(galleryUrl, UriKind.Absolute, out var referer)
            ? referer
            : null;
        if (CookieParser.TryParse(cookie ?? "", out var cookies, out _))
        {
            _cookieHeader = string.Join(
                "; ",
                cookies.Select(pair => $"{pair.Key}={pair.Value}"));
        }
        _enginePath = enginePath;
        _forceIpv4 = forceIpv4;
    }

    public async Task<NodeProbeMeasurement> TestAsync(
        string groupName,
        string nodeName,
        IReadOnlyList<Uri> sampleUrls,
        long byteLimitPerSample,
        int timeoutSeconds,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyList<Uri>>>? refreshSamples = null)
    {
        await _controller.SelectProxyAsync(groupName, nodeName, cancellationToken);
        await Task.Delay(650, cancellationToken);

        var successful = 0;
        var interrupted = 0;
        long totalBytes = 0;
        var totalSeconds = 0d;
        var routeVerified = false;
        var notes = new List<string>();
        IReadOnlyList<Uri>? refreshedUrls = null;
        Exception? refreshError = null;

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
            {
                successful++;
                notes.Add($"样本 {index + 1}：{sample.Message}");
                continue;
            }

            interrupted++;
            notes.Add($"样本 {index + 1}首次尝试：{sample.Message}");
            Uri? retryUrl = null;
            if (!string.IsNullOrWhiteSpace(_enginePath)
                && File.Exists(_enginePath))
            {
                progress?.Report(
                    $"首次失败，正在用下载内核重新连接图片服务器");
                retryUrl = sampleUrls[index];
            }
            else if (refreshSamples is not null
                     && refreshedUrls is null
                     && refreshError is null)
            {
                progress?.Report("首次失败，正在通过此节点重新分配图片服务器");
                try
                {
                    refreshedUrls = await refreshSamples(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    refreshError = ex;
                    notes.Add($"重新分配失败：{FriendlyNetworkError(ex)}");
                }
            }

            if (retryUrl is null && refreshedUrls is { Count: > 0 })
                retryUrl = SelectRetryUrl(refreshedUrls, sampleUrls[index], index);
            if (retryUrl is null) continue;
            progress?.Report($"已重新分配，复测样本 {index + 1}/{sampleUrls.Count}");
            var retry = await DownloadSampleAsync(
                retryUrl,
                nodeName,
                byteLimitPerSample,
                timeoutSeconds,
                cancellationToken);
            totalBytes += retry.ReceivedBytes;
            totalSeconds += retry.ElapsedSeconds;
            routeVerified |= retry.RouteVerified;
            if (retry.Success)
            {
                successful++;
                notes.Add(
                    $"样本 {index + 1}复测：重新分配到 {retryUrl.Host} 后传输正常，"
                    + $"读取 {FormatMb(retry.ReceivedBytes)}");
            }
            else
            {
                interrupted++;
                notes.Add($"样本 {index + 1}复测：{retry.Message}");
            }
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
        if (!string.IsNullOrWhiteSpace(_enginePath)
            && File.Exists(_enginePath))
        {
            try
            {
                var resolved = await ResolveDownloadUrlAsync(
                    url,
                    timeoutSeconds,
                    cancellationToken);
                return await DownloadWithGalleryDlAsync(
                    resolved,
                    nodeName,
                    byteLimit,
                    timeoutSeconds,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new SampleMeasurement(
                    false,
                    0,
                    Math.Max(timeoutSeconds, 0.001),
                    false,
                    "下载内核连接超时，只读取 0.0 MB");
            }
            catch (Exception ex)
            {
                return new SampleMeasurement(
                    false,
                    0,
                    0.001,
                    false,
                    $"{FriendlyNetworkError(ex)}，只读取 0.0 MB");
            }
        }

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
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

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
            if (_referer is not null)
                request.Headers.Referrer = _referer;
            if (!string.IsNullOrWhiteSpace(_cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new SampleMeasurement(
                    false,
                    0,
                    0.001,
                    false,
                    $"图片服务器返回 HTTP {(int)response.StatusCode} "
                    + $"({response.ReasonPhrase ?? "未知状态"})");
            }

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

    private async Task<Uri> ResolveDownloadUrlAsync(
        Uri url,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(_proxy),
            UseProxy = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        using var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 8)));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        request.Headers.Accept.ParseAdd("image/avif,image/webp,image/*,*/*;q=0.8");
        if (_referer is not null) request.Headers.Referrer = _referer;
        if (!string.IsNullOrWhiteSpace(_cookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);

        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices
            and < HttpStatusCode.BadRequest
            && response.Headers.Location is { } location)
        {
            return location.IsAbsoluteUri
                ? location
                : new Uri(url, location);
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"图片入口返回 HTTP {(int)response.StatusCode} "
                + $"({response.ReasonPhrase ?? "未知状态"})");
        return url;
    }

    private async Task<SampleMeasurement> DownloadWithGalleryDlAsync(
        Uri url,
        string nodeName,
        long byteLimit,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            SettingsStore.DataDirectory,
            $"node-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo
        {
            FileName = _enginePath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = GalleryDlProcessEncoding.Current,
            StandardErrorEncoding = GalleryDlProcessEncoding.Current
        };
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONLEGACYWINDOWSSTDIO"] = "0";
        Add(info, "--config-ignore", "--no-input", "--no-colors");
        Add(info, "--proxy", _proxy.AbsoluteUri);
        Add(info, "--http-timeout", timeoutSeconds.ToString());
        Add(info, "--retries", "0");
        Add(info, "--destination", directory);
        Add(info, "--filename", "sample.{extension}");
        if (_forceIpv4) Add(info, "--force-ipv4");
        info.ArgumentList.Add(url.AbsoluteUri);

        using var process = new Process { StartInfo = info };
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var verifyCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Stopwatch.StartNew();
        Task<bool>? verification = null;
        string stderr = "";
        var capped = false;
        var timedOut = false;
        long received = 0;
        try
        {
            process.Start();
            verification = VerifyRouteAsync(
                url.Host,
                nodeName,
                verifyCancellation.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitTask = process.WaitForExitAsync(timeout.Token);

            while (!exitTask.IsCompleted)
            {
                await Task.Delay(100, timeout.Token);
                received = MeasureDirectoryBytes(directory);
                if (received < byteLimit) continue;
                capped = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                break;
            }

            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await stdoutTask; } catch { }
            try { stderr = await stderrTask; } catch { }
            received = Math.Max(received, MeasureDirectoryBytes(directory));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            received = Math.Max(received, MeasureDirectoryBytes(directory));
        }
        finally
        {
            timer.Stop();
        }

        var verified = await StopVerificationAsync(verification, verifyCancellation);
        var success = received > 0
                      && (capped || (!timedOut && process.ExitCode == 0));
        var message = success
            ? $"下载内核传输正常，读取 {FormatMb(received)}"
            : timedOut
                ? $"下载内核超时，只读取 {FormatMb(received)}"
                : $"{FriendlyNetworkError(new InvalidOperationException(stderr))}，"
                  + $"只读取 {FormatMb(received)}";
        try { Directory.Delete(directory, recursive: true); } catch { }
        return new SampleMeasurement(
            success,
            received,
            Math.Max(timer.Elapsed.TotalSeconds, 0.001),
            verified,
            message);
    }

    private static long MeasureDirectoryBytes(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }

    private static void Add(ProcessStartInfo info, params string[] values)
    {
        foreach (var value in values) info.ArgumentList.Add(value);
    }

    private static Uri SelectRetryUrl(
        IReadOnlyList<Uri> refreshedUrls,
        Uri original,
        int index)
    {
        var differentHost = refreshedUrls.FirstOrDefault(uri =>
            !uri.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase));
        if (differentHost is not null) return differentHost;

        var differentUrl = refreshedUrls.FirstOrDefault(uri => uri != original);
        return differentUrl ?? refreshedUrls[index % refreshedUrls.Count];
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

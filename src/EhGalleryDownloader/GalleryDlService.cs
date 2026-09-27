using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EhGalleryDownloader;

public sealed class GalleryDlService : IDisposable
{
    private Process? _process;
    private readonly object _gate = new();
    private string _lastRawError = "";
    private int _resumeAnchorIndex;
    private readonly DownloadProgressTracker _progressTracker = new();
    private readonly DownloadTransferAccounting _accounting = new();

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    public string LastRawError => _lastRawError;
    public bool LastRunUsedImageAnchor => _resumeAnchorIndex > 0;

    public event Action<string>? OutputReceived;
    public event Action<string>? CurrentFileChanged;
    public event Action<string, int>? GalleryMetadataChanged;
    public event Action<int>? ExistingPrefixDetected;
    public event Action<int>? FileCompleted;
    public event Action<int>? FileSkipped;
    public event Action<int>? FileFailed;
    public event Action<DownloadProgress>? ProgressChanged;

    public async Task<int> RunAsync(
        string enginePath,
        DownloadJob job,
        CancellationToken cancellationToken,
        bool compatibilityResume = false)
    {
        Directory.CreateDirectory(job.OutputDirectory);
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        Directory.CreateDirectory(SettingsStore.LogDirectory);

        var logPath = Path.Combine(SettingsStore.LogDirectory,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{job.Id:N}.log");
        var progressPath = Path.Combine(SettingsStore.DataDirectory,
            $"progress-{job.Id:N}.log");
        try { File.Delete(progressPath); } catch { }
        _lastRawError = "";
        _accounting.Reset();
        _progressTracker.Reset();
        _resumeAnchorIndex = 0;

        GalleryResumePlan? resumePlan;
        try
        {
            resumePlan = await Task.Run(
                () => GalleryResumePlanner.Create(job, compatibilityResume, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return -1;
        }
        _resumeAnchorIndex = resumePlan?.AnchorIndex ?? 0;
        if (resumePlan is not null)
        {
            ExistingPrefixDetected?.Invoke(resumePlan.ExistingPrefixCount);
            if (resumePlan.AlreadyComplete)
            {
                OutputReceived?.Invoke(
                    $"本地前 {resumePlan.ExistingPrefixCount} 张图片齐全，已达到已知总页数。" );
                return cancellationToken.IsCancellationRequested ? -1 : 0;
            }
            OutputReceived?.Invoke(
                $"已在本地确认前 {resumePlan.ExistingPrefixCount} 张完整图片，"
                + $"准备从第 {resumePlan.StartIndex} 张继续。" );
            OutputReceived?.Invoke(resumePlan.AnchorIndex > 0
                ? $"正在通过第 {resumePlan.AnchorIndex} 张已完成图片快速进入断点。"
                : $"正在使用兼容模式从第 {resumePlan.StartIndex} 张继续；定位阶段可能较慢。" );
        }

        var runConfigPath = CreateRunConfig(job);

        var info = new ProcessStartInfo
        {
            FileName = enginePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = GalleryDlProcessEncoding.Current,
            StandardErrorEncoding = GalleryDlProcessEncoding.Current
        };
        ConfigureUtf8Output(info);

        AddCommonArguments(info, job, logPath, runConfigPath, progressPath, resumePlan);
        info.ArgumentList.Add(resumePlan?.InputUrl ?? job.Url);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;
        Task progressTask = Task.CompletedTask;
        using var progressCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressCursor = new ProgressFileCursor();
        try
        {
            process.Start();
            lock (_gate) _process = process;
            using var registration = cancellationToken.Register(Stop);

            stdoutTask = PumpAsync(process.StandardOutput, false, cancellationToken);
            stderrTask = PumpAsync(process.StandardError, true, cancellationToken);
            progressTask = MonitorProgressFileAsync(
                progressPath, progressCursor, progressCancellation.Token);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Stop();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            }
            catch { }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask)
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch { }
            return -1;
        }
        finally
        {
            progressCancellation.Cancel();
            try { await progressTask.ConfigureAwait(false); } catch { }
            DrainProgressFile(progressPath, progressCursor);
            lock (_gate)
            {
                if (ReferenceEquals(_process, process)) _process = null;
            }
            try { File.Delete(runConfigPath); } catch { }
            try { File.Delete(progressPath); } catch { }
        }
    }

    private static void ConfigureUtf8Output(ProcessStartInfo info)
    {
        // gallery-dl is a frozen Python application. Without these settings,
        // Chinese Windows can emit file paths as CP936 while .NET reads UTF-8.
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONLEGACYWINDOWSSTDIO"] = "0";
        info.Environment["PYTHONUNBUFFERED"] = "1";
    }

    private static void AddCommonArguments(
        ProcessStartInfo info,
        DownloadJob job,
        string logPath,
        string runConfigPath,
        string progressPath,
        GalleryResumePlan? resumePlan)
    {
        Add(info, "--config-ignore");
        Add(info, "--config-json", runConfigPath);
        Add(info, "--no-input");
        Add(info, "--no-colors");
        Add(info, "--windows-filenames");
        Add(info, "--destination", job.OutputDirectory);
        Add(info, "--write-log", logPath);
        Add(info, "--Print-to-file", "post:__GUI_META__|{gid}|{filecount}|{title}", progressPath);
        Add(info, "--Print-to-file", "prepare:__GUI_PREPARE__|{num}|{filecount}|{_path}", progressPath);
        Add(info, "--Print-to-file", "after:__GUI_SUCCESS__|{num}|{filecount}|{_path}", progressPath);
        Add(info, "--Print-to-file", "skip:__GUI_SKIP__|{num}|{filecount}|{_path}", progressPath);
        Add(info, "--Print-to-file", "error:__GUI_FAILURE__|{num}|{filecount}|{_path}", progressPath);
        Add(info, "--http-timeout", "35");
        Add(info, "--retries", "4");
        Add(info, "--sleep-retries", "2.0-5.0");
        if (resumePlan?.Range is { } range)
            Add(info, "--range", range);
        Add(info, "--option", "extractor.exhentai.fallback-retries=2");
        Add(info, "--option", "extractor.exhentai.gp=resized");
        Add(info, "--option", $"extractor.exhentai.original={job.Options.DownloadOriginal.ToString().ToLowerInvariant()}");

        if (job.Options.UseProxy)
            Add(info, "--proxy", job.Options.ProxyUrl);
        if (job.Options.UseBrowserCookies)
            Add(info, "--cookies-from-browser", job.Options.Browser);
        if (job.Options.ForceIpv4)
            Add(info, "--force-ipv4");
        if (job.Options.WriteMetadata)
            Add(info, "--write-info-json");
        if (job.Options.PackageAsCbz)
            Add(info, "--cbz");
    }

    private static string CreateRunConfig(DownloadJob job)
    {
        var path = Path.Combine(SettingsStore.DataDirectory, $"run-{job.Id:N}.json");
        var config = new Dictionary<string, object>();
        if (job.Options.LoginMode == "manual"
            && CookieParser.TryParse(job.Options.ManualCookie, out var cookies, out _))
        {
            config["extractor"] = new Dictionary<string, object>
            {
                ["exhentai"] = new Dictionary<string, object>
                {
                    ["cookies"] = cookies,
                    ["cookies-update"] = false
                }
            };
        }
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        try
        {
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary);
        }
        catch { }
        return path;
    }

    private async Task PumpAsync(StreamReader reader, bool isError, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (line is null) break;
            ParseLine(line, isError);
        }
    }

    private void ParseLine(string raw, bool isError)
    {
        var line = StripAnsi(raw).Trim();
        if (line.Length == 0) return;
        if (line.Contains('\uFFFD'))
        {
            if (isError) _lastRawError = "下载内核返回了无法解码的文字。";
            OutputReceived?.Invoke("正在处理文件（文件名包含当前编码无法显示的字符）。");
            return;
        }

        if (GalleryDlOutputParser.TryParse(line, out var marker))
        {
            if (marker.Kind == GalleryDlEventKind.Metadata)
            {
                GalleryMetadataChanged?.Invoke(marker.Path, marker.Total);
                return;
            }
            CurrentFileChanged?.Invoke(ShortenPath(marker.Path));
            switch (marker.Kind)
            {
                case GalleryDlEventKind.Prepare:
                    _accounting.RecordPrepare(marker.Path);
                    ReportProgress(marker);
                    break;
                case GalleryDlEventKind.Success:
                case GalleryDlEventKind.Skip:
                case GalleryDlEventKind.Failure:
                    // The existing anchor was already included in the local prefix.
                    if (marker.Current == _resumeAnchorIndex) break;
                    if (_accounting.TryRecordTerminal(
                            marker.Kind, marker.Path, out var previous))
                    {
                        if (previous is { } old) ReportFileDelta(old, -1);
                        ReportFileDelta(marker.Kind, 1);
                        ReportProgress(marker);
                    }
                    break;
            }
            return;
        }

        if (isError && (line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("error:", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("exception", StringComparison.OrdinalIgnoreCase)))
        {
            _lastRawError = line;
        }

        OutputReceived?.Invoke(ToChineseMessage(line));
    }

    private sealed class ProgressFileCursor
    {
        public int LinesRead { get; set; }
    }

    private async Task MonitorProgressFileAsync(
        string path,
        ProgressFileCursor cursor,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DrainProgressFile(path, cursor);
            try
            {
                await Task.Delay(180, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void DrainProgressFile(string path, ProgressFileCursor cursor)
    {
        try
        {
            if (!File.Exists(path)) return;
            var lines = new List<string>();
            using (var stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(
                       stream, new UTF8Encoding(false, false),
                       detectEncodingFromByteOrderMarks: true))
            {
                while (reader.ReadLine() is { } line) lines.Add(line);
            }

            if (lines.Count < cursor.LinesRead) cursor.LinesRead = 0;
            for (var index = cursor.LinesRead; index < lines.Count; index++)
                ParseLine(lines[index], isError: false);
            cursor.LinesRead = lines.Count;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ReportFileDelta(GalleryDlEventKind kind, int delta)
    {
        switch (kind)
        {
            case GalleryDlEventKind.Success: FileCompleted?.Invoke(delta); break;
            case GalleryDlEventKind.Skip: FileSkipped?.Invoke(delta); break;
            case GalleryDlEventKind.Failure: FileFailed?.Invoke(delta); break;
        }
    }

    private void ReportProgress(GalleryDlEvent marker)
    {
        ProgressChanged?.Invoke(_progressTracker.Record(
            marker.Current,
            marker.Total,
            _accounting.DownloadedBytes,
            _accounting.ProcessedFiles));
    }

    private static string ShortenPath(string path)
    {
        try { return Path.GetFileName(path.Trim()); }
        catch { return path.Trim(); }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    private static string ToChineseMessage(string line)
    {
        if (line.Contains("KeyError - 'i3'", StringComparison.OrdinalIgnoreCase))
            return "下载内核兼容错误（KeyError i3）。继续重复点击不会解决；请先更新内核，再继续这个任务。";
        if (line.Contains("cookies", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)))
            return "读取浏览器登录信息失败：浏览器正在占用 Cookie 文件。请完全退出所选浏览器后重试，或选择“不读取登录信息”。";
        if (line.Contains("UNEXPECTED_EOF_WHILE_READING", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                && line.Contains("EOF", StringComparison.OrdinalIgnoreCase)))
            return "当前 Clash 节点与图片服务器的加密连接被中途切断，内核正在有限重试；如果反复出现，请停止任务并更换节点。";
        if (line.Contains("AuthenticationError", StringComparison.OrdinalIgnoreCase)
            || line.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "登录状态无效。请在所选浏览器中重新登录网站。";
        if (line.Contains("429", StringComparison.OrdinalIgnoreCase))
            return "网站提示请求过于频繁（429），内核正在按间隔重试。";
        if (line.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "网络连接超时，内核正在重试；如果持续发生，可以换 Clash 节点后继续。";
        if (line.Contains("unsupported URL", StringComparison.OrdinalIgnoreCase))
            return "这个链接暂不受支持，请粘贴画廊首页链接，而不是单张图片链接。";
        if (line.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("404", StringComparison.OrdinalIgnoreCase))
            return "网页或图片不存在（404），也可能是登录权限不足。";
        return line;
    }

    private static string StripAnsi(string value) =>
        Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]", "");

    private static void Add(ProcessStartInfo info, params string[] values)
    {
        foreach (var value in values) info.ArgumentList.Add(value);
    }

    public void Dispose() => Stop();

}

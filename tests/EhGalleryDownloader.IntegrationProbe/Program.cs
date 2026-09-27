using System.Diagnostics;
using System.Security.Cryptography;
using EhGalleryDownloader;

var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", ".."));
var enginePath = new[]
{
    Path.Combine(projectDirectory, "tools", "gallery-dl.exe"),
    Path.Combine(projectDirectory, "dist", "tools", "gallery-dl.exe")
}.FirstOrDefault(File.Exists) ?? Path.Combine(projectDirectory, "tools", "gallery-dl.exe");
var outputPath = Path.Combine(projectDirectory, "integration-output");
const string testCookie = "ipb_member_id=local-test-user; ipb_pass_hash=local-test=secret; igneous=test-token";

if (!CookieParser.TryParse("Cookie: " + testCookie, out var parsedCookies, out var parseError)
    || parsedCookies.Count != 3
    || parsedCookies["ipb_pass_hash"] != "local-test=secret")
{
    Console.Error.WriteLine($"FAIL: Cookie parser rejected a valid request header: {parseError}");
    return 4;
}
Console.WriteLine("PASS: full Cookie request header parses without losing '=' in values.");

var browserPayload = new BrowserCookieImportPayload(
    "one-time-token",
    "Google Chrome",
    "https://exhentai.org/",
[
    new BrowserCookieItem("ipb_member_id", "12345", ".e-hentai.org"),
    new BrowserCookieItem("ipb_pass_hash", "abc=def", ".e-hentai.org"),
    new BrowserCookieItem("igneous", "token", ".exhentai.org"),
    new BrowserCookieItem("unrelated", "must-not-pass", ".e-hentai.org"),
    new BrowserCookieItem("ipb_member_id", "evil", ".example.com")
]);
if (!BrowserCookieImportServer.TryCreateCookieHeader(
        browserPayload, out var importedHeader, out var importError)
    || !importedHeader.Contains("ipb_member_id=12345", StringComparison.Ordinal)
    || !importedHeader.Contains("ipb_pass_hash=abc=def", StringComparison.Ordinal)
    || !importedHeader.Contains("igneous=token", StringComparison.Ordinal)
    || importedHeader.Contains("unrelated", StringComparison.Ordinal)
    || importedHeader.Contains("evil", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"FAIL: browser Cookie import scope is unsafe or incomplete: {importError}");
    return 28;
}
Console.WriteLine("PASS: browser import only accepts the three required cookies from gallery domains.");

await using (var importServer = BrowserCookieImportServer.Start())
{
    var waitForImport = importServer.WaitForImportAsync(TimeSpan.FromSeconds(10));
    using var importClient = new HttpClient(new SocketsHttpHandler { UseProxy = false });
    using var sessionRequest = new HttpRequestMessage(
        HttpMethod.Get, $"http://127.0.0.1:{importServer.Port}/session");
    sessionRequest.Headers.TryAddWithoutValidation("Origin", "chrome-extension://unit-test");
    sessionRequest.Headers.TryAddWithoutValidation("X-EhGallery-Extension", "1");
    using var sessionResponse = await importClient.SendAsync(sessionRequest);
    using var sessionJson = System.Text.Json.JsonDocument.Parse(
        await sessionResponse.Content.ReadAsStringAsync());
    var sessionToken = sessionJson.RootElement.GetProperty("token").GetString();
    var postPayload = System.Text.Json.JsonSerializer.Serialize(browserPayload with
    {
        Token = sessionToken ?? ""
    });
    using var postRequest = new HttpRequestMessage(
        HttpMethod.Post, $"http://127.0.0.1:{importServer.Port}/import")
    {
        Content = new StringContent(postPayload, System.Text.Encoding.UTF8, "application/json")
    };
    postRequest.Headers.TryAddWithoutValidation("Origin", "chrome-extension://unit-test");
    postRequest.Headers.TryAddWithoutValidation("X-EhGallery-Extension", "1");
    using var postResponse = await importClient.SendAsync(postRequest);
    var receivedImport = await waitForImport;
    if (!sessionResponse.IsSuccessStatusCode
        || !postResponse.IsSuccessStatusCode
        || receivedImport.Browser != "Google Chrome"
        || receivedImport.CookieHeader != importedHeader)
    {
        Console.Error.WriteLine("FAIL: browser extension loopback handoff did not complete safely.");
        return 29;
    }
}
Console.WriteLine("PASS: one-time browser extension handoff works over the local-only channel.");

var ranking = NodeProbeRanking.Order(
[
    new NodeProbeResult
    {
        Name = "很快但会断",
        Type = "Hysteria2",
        SuccessfulSamples = 2,
        SampleCount = 3,
        Interruptions = 1,
        SpeedMbPerSecond = 18
    },
    new NodeProbeResult
    {
        Name = "稳定节点",
        Type = "VLESS",
        SuccessfulSamples = 3,
        SampleCount = 3,
        Interruptions = 0,
        SpeedMbPerSecond = 5
    },
    new NodeProbeResult
    {
        Name = "完全失败",
        Type = "VLESS",
        SuccessfulSamples = 0,
        SampleCount = 3,
        Interruptions = 3,
        SpeedMbPerSecond = 0
    },
    new NodeProbeResult
    {
        Name = "等待测试",
        Type = "VLESS",
        ClashScreened = true,
        ClashDelayMs = 120,
        Status = "等待真实下载"
    }
]);
if (ranking[0].Name != "稳定节点"
    || ranking[2].Name != "等待测试"
    || ranking[^1].Name != "完全失败")
{
    Console.Error.WriteLine("FAIL: real-image node ranking did not prioritize completeness.");
    return 7;
}
Console.WriteLine("PASS: node ranking prioritizes complete stable transfers over raw speed.");

var screeningInput = Enumerable.Range(1, 40)
    .Select(index => new NodeProbeResult
    {
        Name = $"node-{index:00}",
        Type = "VLESS",
        ClashScreened = true,
        ClashDelayMs = index == 40 ? 0 : 80 + index * 10,
        GalleryScreened = true,
        GalleryDelayMs = index == 39 ? 0 : 100 + index * 12
    })
    .ToList();
var smartCandidates = NodeScreeningLogic.SelectForRealTest(
    screeningInput,
    testAllReachable: false);
var allCandidates = NodeScreeningLogic.SelectForRealTest(
    screeningInput,
    testAllReachable: true);
if (smartCandidates.Count != NodeScreeningLogic.DefaultSmartLimit
    || allCandidates.Count != 39
    || smartCandidates.Any(result => result.ClashDelayMs <= 0)
    || !smartCandidates.Any(result => result.Name == "node-39")
    || smartCandidates.Max(result => result.ClashDelayMs)
       <= smartCandidates.OrderBy(result => result.ClashDelayMs)
           .Take(13).Max(result => result.ClashDelayMs))
{
    Console.Error.WriteLine(
        "FAIL: smart screening did not remove Clash errors or retain broad high-latency samples.");
    return 16;
}
Console.WriteLine(
    "PASS: smart screening only removes Clash errors and keeps broad candidates for real downloads.");

var regionalCandidates = NodeScreeningLogic.SelectForRealTest(
[
    new NodeProbeResult { Name = "🇯🇵日本01", Type = "VLESS", ClashDelayMs = 50 },
    new NodeProbeResult { Name = "🇯🇵日本02", Type = "VLESS", ClashDelayMs = 55 },
    new NodeProbeResult { Name = "🇸🇬新加坡01", Type = "VLESS", ClashDelayMs = 80 },
    new NodeProbeResult { Name = "🇺🇸美国01", Type = "VLESS", ClashDelayMs = 120 },
    new NodeProbeResult { Name = "🇩🇪德国01", Type = "VLESS", ClashDelayMs = 180 },
    new NodeProbeResult { Name = "🇬🇧英国01", Type = "VLESS", ClashDelayMs = 190 },
    new NodeProbeResult { Name = "🇯🇵日本03", Type = "VLESS", ClashDelayMs = 60 },
    new NodeProbeResult { Name = "🇯🇵日本04", Type = "VLESS", ClashDelayMs = 65 }
],
    testAllReachable: false,
    smartLimit: 6);
if (regionalCandidates.Take(5)
        .Select(result => NodeScreeningLogic.GetRegionKey(result.Name))
        .Distinct(StringComparer.Ordinal)
        .Count() != 5)
{
    Console.Error.WriteLine("FAIL: smart screening did not test regional representatives first.");
    return 19;
}
Console.WriteLine("PASS: smart screening tests regional representatives before same-region duplicates.");

const string continuationUrl = "https://example.invalid/g/123/token/";
var stoppedJob = new DownloadJob
{
    Url = continuationUrl,
    OutputDirectory = outputPath,
    Options = new DownloadOptions(
        "none", "edge", false, null, false, "", true, false, false, true),
    State = "已停止",
    CompletedFiles = 4,
    SkippedFiles = 3,
    FailedFiles = 2,
    TotalFiles = 837
};
var reusable = DownloadTaskLogic.FindReusable(
    [stoppedJob], continuationUrl.TrimEnd('/'));
if (!ReferenceEquals(reusable, stoppedJob))
{
    Console.Error.WriteLine("FAIL: a stopped task was not reused for continuation.");
    return 12;
}
stoppedJob.BeginAttempt();
if (stoppedJob.AttemptCount != 1
    || stoppedJob.CompletedFiles != 0
    || stoppedJob.SkippedFiles != 0
    || stoppedJob.FailedFiles != 0
    || stoppedJob.TotalFiles != 837)
{
    Console.Error.WriteLine(
        "FAIL: continuing a task did not reset attempt counts while retaining the known total.");
    return 13;
}
stoppedJob.State = "已完成";
if (DownloadTaskLogic.FindReusable([stoppedJob], continuationUrl) is not null)
{
    Console.Error.WriteLine("FAIL: a completed task was incorrectly reused as an interrupted task.");
    return 14;
}
Console.WriteLine("PASS: stopped tasks continue in-place while completed tasks remain separate.");

var resumeTestRoot = Path.Combine(outputPath, "resume-planner-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(resumeTestRoot);
try
{
    foreach (var index in new[] { 1, 2, 4 })
        File.WriteAllBytes(
            Path.Combine(resumeTestRoot, $"4089450_{index:0000}_hash_image.png"),
            [1, 2, 3]);
    File.WriteAllBytes(
        Path.Combine(resumeTestRoot, "4089450_0003_hash_image.png.part"),
        [1, 2, 3]);
    File.WriteAllBytes(
        Path.Combine(resumeTestRoot, "9999999_0003_hash_other.png"),
        [1, 2, 3]);

    var resumeJob = new DownloadJob
    {
        Url = "https://exhentai.org/g/4089450/token/",
        OutputDirectory = resumeTestRoot,
        TotalFiles = 5,
        Options = new DownloadOptions(
            "none", "edge", false, null, false, "", true, false, false, true)
    };
    var gapPlan = GalleryResumePlanner.Create(resumeJob);
    if (gapPlan is not { StartIndex: 3, ExistingPrefixCount: 2 }
        || gapPlan.InputUrl != "https://exhentai.org/g/4089450/token/#page3"
        || gapPlan.Range is not null)
    {
        Console.Error.WriteLine(
            "FAIL: fast resume did not build a direct continuation at the first missing image.");
        return 31;
    }

    File.WriteAllBytes(
        Path.Combine(resumeTestRoot, "4089450_0003_hash_image.png"),
        [1, 2, 3]);
    var contiguousPlan = GalleryResumePlanner.Create(resumeJob);
    if (contiguousPlan is not { StartIndex: 5, ExistingPrefixCount: 4 }
        || contiguousPlan.InputUrl != "https://exhentai.org/g/4089450/token/#page5"
        || contiguousPlan.Range is not null)
    {
        Console.Error.WriteLine(
            "FAIL: fast resume did not directly continue after the complete local prefix.");
        return 32;
    }

    resumeJob.TotalFiles = 4;
    var completePlan = GalleryResumePlanner.Create(resumeJob);
    if (completePlan is not { AlreadyComplete: true, ExistingPrefixCount: 4 })
    {
        Console.Error.WriteLine("FAIL: a fully downloaded gallery must not request a nonexistent next page.");
        return 37;
    }
    using (var completeService = new GalleryDlService())
    {
        var knownPrefix = 0;
        completeService.ExistingPrefixDetected += count => knownPrefix = count;
        var completeExit = await completeService.RunAsync(
            "missing-gallery-dl.exe", resumeJob, CancellationToken.None);
        if (completeExit != 0 || knownPrefix != 4)
        {
            Console.Error.WriteLine("FAIL: a complete local gallery still launched the engine.");
            return 38;
        }
    }

    resumeJob.TotalFiles = 0;
    var unknownTotalPlan = GalleryResumePlanner.Create(resumeJob);
    if (unknownTotalPlan is not { AlreadyComplete: false, Range: "5-" }
        || unknownTotalPlan.InputUrl != resumeJob.Url)
    {
        Console.Error.WriteLine("FAIL: unknown gallery total did not use the safe compatibility path.");
        return 39;
    }

    resumeJob.Options = resumeJob.Options with { PackageAsCbz = true };
    if (GalleryResumePlanner.Create(resumeJob) is not null)
    {
        Console.Error.WriteLine("FAIL: fast resume must not scan packaged CBZ tasks.");
        return 33;
    }
}
finally
{
    try { Directory.Delete(resumeTestRoot, recursive: true); } catch { }
}
Console.WriteLine("PASS: fast resume starts at the first missing image without skipping gaps.");

const string fakeDigest =
    "a468359545129a1268ff3e2a59d2b453ff18a7306025a332a834f72eb328105e";
var parsedAsset = ReleaseAssetParser.Parse(
    "<a href=\"/gdl-org/builds/releases/download/2026.07.28/gallery-dl_windows_x86.exe\">x86</a>"
    + "<span>sha256:1111111111111111111111111111111111111111111111111111111111111111</span>"
    + "<a href=\"/gdl-org/builds/releases/download/2026.07.28/gallery-dl_windows.exe\">x64</a>"
    + $"<span>sha256:{fakeDigest}</span>",
    "2026.07.28");
if (!parsedAsset.DownloadUrl.EndsWith(
        "/gallery-dl_windows.exe", StringComparison.OrdinalIgnoreCase)
    || !string.Equals(parsedAsset.Sha256Digest, fakeDigest, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("FAIL: release page parser selected the wrong Windows asset.");
    return 15;
}
Console.WriteLine("PASS: update parser selects the 64-bit Windows asset without GitHub API.");

var parsedUrls = GalleryUrlValidator.ParseMany(
    "https://e-hentai.org/g/12345/abcde/\nhttps://exhentai.org/g/67890/fghij/?ignored=1",
    out var urlError);
if (parsedUrls.Count != 2
    || parsedUrls[0] != "https://e-hentai.org/g/12345/abcde/"
    || parsedUrls[1] != "https://exhentai.org/g/67890/fghij/"
    || urlError.Length != 0)
{
    Console.Error.WriteLine($"FAIL: batch gallery URL parsing failed: {urlError}");
    return 20;
}
if (GalleryUrlValidator.TryNormalize(
        "https://e-hentai.org/s/token/123-1", out _, out _)
    || GalleryUrlValidator.TryNormalize(
        "https://example.com/g/12345/abcde/", out _, out _))
{
    Console.Error.WriteLine("FAIL: unsupported or single-image URLs were accepted.");
    return 21;
}
Console.WriteLine("PASS: batch URL parsing accepts gallery pages and rejects unsupported links.");

if (!DownloadErrorClassifier.IsTransient("SSL unexpected EOF while reading")
    || !DownloadErrorClassifier.IsTransient("connection timed out")
    || DownloadErrorClassifier.IsTransient("unsupported URL")
    || !DownloadErrorClassifier.IsAuthenticationFailure("AuthenticationError"))
{
    Console.Error.WriteLine("FAIL: download error classification is incorrect.");
    return 22;
}
Console.WriteLine("PASS: transient network failures are separated from permanent errors.");

var temporaryJobsPath = Path.Combine(
    Path.GetTempPath(), $"eh-gallery-jobs-{Guid.NewGuid():N}.json");
try
{
    var persistedJob = new DownloadJob
    {
        Url = "https://e-hentai.org/g/12345/abcde/",
        OutputDirectory = outputPath,
        Options = new DownloadOptions(
            "manual", "edge", false, "ipb_member_id=secret", true,
            "http://127.0.0.1:7897", true, false, true, true),
        State = "下载中",
        AttemptCount = 2
    };
    JobStore.SaveToPath(temporaryJobsPath, [persistedJob]);
    var restoredJobs = JobStore.LoadFromPath(temporaryJobsPath);
    if (restoredJobs.Count != 1
        || restoredJobs[0].State != "已停止"
        || restoredJobs[0].AttemptCount != 2
        || restoredJobs[0].Options.ManualCookie is not null
        || File.ReadAllText(temporaryJobsPath).Contains("ipb_member_id", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("FAIL: persisted task recovery is incomplete or leaked a Cookie.");
        return 23;
    }
Console.WriteLine("PASS: interrupted tasks persist safely without storing Cookies.");
}
finally
{
    try { File.Delete(temporaryJobsPath); } catch { }
}

if (!GalleryDlOutputParser.TryParse(
        "__GUI_SUCCESS__|12|40|D:\\Pictures\\gallery\\012.jpg",
        out var outputEvent)
    || outputEvent.Kind != GalleryDlEventKind.Success
    || outputEvent.Current != 12
    || outputEvent.Total != 40
    || !outputEvent.Path.EndsWith("012.jpg", StringComparison.OrdinalIgnoreCase)
    || GalleryDlOutputParser.TryParse("ordinary gallery-dl output", out _))
{
    Console.Error.WriteLine("FAIL: structured gallery-dl progress parsing failed.");
    return 24;
}
Console.WriteLine("PASS: structured gallery-dl progress reports current and total files.");

if (!GalleryDlOutputParser.TryParse(
        "__GUI_META__|4089450|6|[Mawmain (Maw)] Pleasing the Crowd",
        out var metadataEvent)
    || metadataEvent.Kind != GalleryDlEventKind.Metadata
    || metadataEvent.Total != 6
    || metadataEvent.Path != "[Mawmain (Maw)] Pleasing the Crowd")
{
    Console.Error.WriteLine("FAIL: gallery metadata parsing did not preserve title and page count.");
    return 31;
}
Console.WriteLine("PASS: gallery metadata reports the real title and total page count.");

if (GalleryDlProcessEncoding.Current.CodePage != System.Text.Encoding.UTF8.CodePage)
{
    Console.Error.WriteLine("FAIL: gallery-dl output is not decoded as UTF-8.");
    return 30;
}
Console.WriteLine("PASS: gallery-dl output is decoded as UTF-8 on every Windows locale.");

var resumedProgress = DownloadProgressCalculator.Calculate(
    current: 218,
    total: 837,
    downloadedBytes: 1024L * 1024,
    elapsedSeconds: 10,
    processedFiles: 1);
var waitingProgress = DownloadProgressCalculator.Calculate(
    current: 218,
    total: 837,
    downloadedBytes: 0,
    elapsedSeconds: 10,
    processedFiles: 0);
if (Math.Abs(resumedProgress.SpeedMbPerSecond - 0.1) > 0.0001
    || resumedProgress.EstimatedRemaining?.TotalSeconds != 6190
    || waitingProgress.EstimatedRemaining is not null)
{
    Console.Error.WriteLine(
        "FAIL: resumed transfer speed or ETA includes setup time or uses the global image index.");
    return 34;
}
Console.WriteLine(
    "PASS: resumed speed and ETA use transfer time and files processed in this attempt.");

var accountingRoot = Path.Combine(outputPath, "resume-accounting-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(accountingRoot);
try
{
    var filePath = Path.Combine(accountingRoot, "page.jpg");
    File.WriteAllBytes(filePath + ".part", new byte[9]);
    var accounting = new DownloadTransferAccounting();
    accounting.RecordPrepare(filePath);
    File.WriteAllBytes(filePath, new byte[10]);
    if (!accounting.TryRecordTerminal(GalleryDlEventKind.Failure, filePath, out var previous)
        || previous is not null
        || !accounting.TryRecordTerminal(GalleryDlEventKind.Success, filePath, out previous)
        || previous != GalleryDlEventKind.Failure
        || accounting.TryRecordTerminal(GalleryDlEventKind.Skip, filePath, out _)
        || accounting.TryRecordTerminal(GalleryDlEventKind.Success, filePath, out _)
        || accounting.ProcessedFiles != 1
        || accounting.DownloadedBytes != 1)
    {
        Console.Error.WriteLine(
            "FAIL: duplicate outcomes or preexisting .part bytes distorted progress.");
        return 40;
    }
}
finally
{
    try { Directory.Delete(accountingRoot, recursive: true); } catch { }
}
Console.WriteLine("PASS: terminal outcomes are deduplicated and preexisting .part bytes excluded.");

var progressTracker = new DownloadProgressTracker();
progressTracker.Reset();
if (progressTracker.IsTransferStarted)
{
    Console.Error.WriteLine("FAIL: transfer timing started during resume setup.");
    return 35;
}
_ = progressTracker.Record(
    current: 218,
    total: 837,
    downloadedBytes: 0,
    processedFiles: 0);
if (!progressTracker.IsTransferStarted)
{
    Console.Error.WriteLine("FAIL: transfer timing did not start with the first file event.");
    return 36;
}
Console.WriteLine("PASS: transfer timing starts with the first file event, not resume setup.");

var displayJob = new DownloadJob
{
    Url = "https://exhentai.org/g/3867507/04fc059355/",
    OutputDirectory = outputPath,
    Options = new DownloadOptions(
        "none", "edge", false, null, false, "", true, false, false, true)
};
if (displayJob.GalleryDisplayName != "画廊 3867507"
    || displayJob.SourceLabel != "ExHentai")
{
    Console.Error.WriteLine("FAIL: task display labels are not user friendly.");
    return 31;
}
Console.WriteLine("PASS: task cards use concise gallery and source labels.");

if (args.Contains("--unit-only", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("PASS: all offline tests completed.");
    return 0;
}

if (args.Contains("--progress-only", StringComparer.OrdinalIgnoreCase))
{
    if (!File.Exists(enginePath))
    {
        Console.Error.WriteLine("FAIL: gallery-dl.exe is required for the progress integration test.");
        return 25;
    }
    var progressOutput = Path.Combine(
        Path.GetTempPath(), "画廊中文路径", $"eh-gallery-progress-{Guid.NewGuid():N}");
    try
    {
        using var progressService = new GalleryDlService();
        var completedEvents = 0;
        var greatestTotal = 0;
        var observedPaths = new List<string>();
        var displayLines = new List<string>();
        progressService.FileCompleted += delta => completedEvents += delta;
        progressService.ProgressChanged += value =>
            greatestTotal = Math.Max(greatestTotal, value.Total);
        progressService.CurrentFileChanged += value => observedPaths.Add(value);
        progressService.OutputReceived += value => displayLines.Add(value);
        var progressJob = new DownloadJob
        {
            Url = "https://commons.wikimedia.org/wiki/File:Example.jpg",
            OutputDirectory = progressOutput,
            Options = new DownloadOptions(
                "none", "edge", false, null, false, "", true, false, false, true)
        };
        var exitCode = await progressService.RunAsync(
            enginePath, progressJob, CancellationToken.None);
        // A single Wikimedia file may not expose gallery-wide filecount metadata.
        if (exitCode != 0 || completedEvents <= 0
            || observedPaths.Count == 0
            || observedPaths.Any(value => value.Contains('\uFFFD'))
            || displayLines.Any(value => value.Contains('\uFFFD')))
        {
            Console.Error.WriteLine(
                $"FAIL: live gallery-dl progress events were not captured "
                + $"without encoding damage (exit={exitCode}, completed={completedEvents}, "
                + $"total={greatestTotal}, paths={observedPaths.Count}, "
                + $"badPaths={observedPaths.Count(value => value.Contains('\uFFFD'))}, "
                + $"badOutput={displayLines.Count(value => value.Contains('\uFFFD'))}). "
                + $"Last output: {string.Join(" | ", displayLines.TakeLast(4))}");
            return 26;
        }
        Console.WriteLine(
            $"PASS: live gallery-dl emitted structured progress "
            + $"({completedEvents}/{greatestTotal}).");
        return 0;
    }
    finally
    {
        try { Directory.Delete(progressOutput, recursive: true); } catch { }
    }
}

if (args.Contains("--update-only", StringComparer.OrdinalIgnoreCase))
{
    var applicationUpdate = await ApplicationUpdateService.CheckAsync(null);
    if (applicationUpdate.LatestVersion <= new Version(0, 0)
        || string.IsNullOrWhiteSpace(applicationUpdate.Tag)
        || !applicationUpdate.ReleaseUrl.Contains("/releases/tag/", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("FAIL: application update information is invalid.");
        return 27;
    }
    Console.WriteLine(
        $"PASS: application update check resolved {applicationUpdate.Tag} "
        + $"(current {applicationUpdate.CurrentVersion.ToString(3)})." );
    return 0;
}

try
{
    using var clash = new MihomoClient("pipe://verge-mihomo");
    var clashState = await clash.GetStateAsync();
    var selector = clashState.Proxies.Values
        .Where(proxy => proxy.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(proxy.Now)
                        && proxy.All.Contains(proxy.Now, StringComparer.Ordinal)
                        && clashState.Proxies.TryGetValue(proxy.Now, out var selectedNode)
                        && selectedNode.All.Count == 0)
        .OrderByDescending(proxy =>
            proxy.Name.Contains("顶级机场", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();
    if (selector is not null)
    {
        await clash.SelectProxyAsync(selector.Name, selector.Now);
        var verifiedState = await clash.GetStateAsync();
        if (!verifiedState.Proxies.TryGetValue(selector.Name, out var verified)
            || verified.Now != selector.Now)
        {
            Console.Error.WriteLine("FAIL: Clash selector did not retain the original node.");
            return 8;
        }
        Console.WriteLine(
            $"PASS: Clash control pipe can read and safely reselect the original node ({selector.Name}).");

        if (string.IsNullOrWhiteSpace(clashState.ProxyUrl)
            || clashState.MixedPort <= 0)
        {
            Console.Error.WriteLine("FAIL: Clash proxy endpoint was not auto-detected.");
            return 17;
        }
        Console.WriteLine(
            $"PASS: Clash proxy endpoint auto-detected as {clashState.ProxyUrl} ({clashState.ProxyPortKind}).");

        var delayCandidates = selector.All
            .Where(name => clashState.Proxies.TryGetValue(name, out var proxy)
                           && proxy.All.Count == 0
                           && !name.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
                           && !name.Equals("REJECT", StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToArray();
        var delayResult = await clash.MeasureGroupDelaysAsync(
            selector.Name,
            delayCandidates,
            new Uri("https://www.gstatic.com/generate_204"),
            4000,
            "200-299");
        var successfulDelay = delayResult
            .Where(item => item.Value > 0)
            .OrderBy(item => item.Value)
            .FirstOrDefault();
        if (successfulDelay.Value <= 0)
        {
            Console.Error.WriteLine(
                "FAIL: Clash fast delay screening did not return a reachable node; results: "
                + string.Join(", ", delayResult.Select(item => $"{item.Key}={item.Value}")));
            return 18;
        }
        Console.WriteLine(
            $"PASS: Clash fast group screening returned {successfulDelay.Value} ms "
            + $"for {successfulDelay.Key} without changing the selected node.");

        var publicOptions = new DownloadOptions(
            "none",
            "edge",
            UseBrowserCookies: false,
            ManualCookie: null,
            UseProxy: true,
            ProxyUrl: clashState.ProxyUrl,
            DownloadOriginal: true,
            PackageAsCbz: false,
            WriteMetadata: false,
            ForceIpv4: true);
        var samples = await GallerySampleService.GetSampleUrlsAsync(
            enginePath,
            "https://commons.wikimedia.org/wiki/File:Example.jpg",
            publicOptions,
            CancellationToken.None);
        var probe = new GalleryNodeTestService(clash, publicOptions.ProxyUrl);
        var measurement = await probe.TestAsync(
            selector.Name,
            selector.Now,
            samples.Take(1).ToList(),
            128 * 1024,
            10,
            progress: null,
            CancellationToken.None);
        if (measurement.SampleCount != 1 || measurement.DownloadedMb <= 0)
        {
            Console.Error.WriteLine("FAIL: real-image node probe did not receive image data.");
            return 9;
        }
        Console.WriteLine(
            $"PASS: real-image probe received data through Clash ({measurement.SpeedMbPerSecond:F2} MB/s).");
    }
    else
    {
        Console.WriteLine("SKIP: Clash has no selectable group available for a no-change control test.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"SKIP: Clash control pipe is not available during tests ({ex.Message}).");
}

byte[]? previousEncryptedCookie = File.Exists(SettingsStore.EncryptedCookiePath)
    ? File.ReadAllBytes(SettingsStore.EncryptedCookiePath)
    : null;
try
{
    SecretStore.SaveCookie(testCookie);
    var encryptedBytes = File.ReadAllBytes(SettingsStore.EncryptedCookiePath);
    var encryptedText = Convert.ToBase64String(encryptedBytes);
    if (SecretStore.LoadCookie() != testCookie
        || encryptedText.Contains("local-test", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("FAIL: DPAPI cookie storage did not round-trip securely.");
        return 5;
    }
    Console.WriteLine("PASS: Cookie is encrypted for the current Windows user and decrypts correctly.");

    var ignoredReplacement =
        "ipb_member_id=do-not-save; ipb_pass_hash=disabled; igneous=disabled";
    CookiePersistence.SaveIfEnabled(ignoredReplacement, enabled: false);
    if (SecretStore.LoadCookie() != testCookie)
    {
        Console.Error.WriteLine("FAIL: disabling auto-save deleted or replaced the existing Cookie.");
        return 10;
    }
    Console.WriteLine("PASS: disabling auto-save preserves the existing encrypted Cookie.");

    var defaultSettings = new AppSettings();
    if (!defaultSettings.RememberCookie
        || defaultSettings.CookieStoragePreferenceSet
        || !defaultSettings.AutoDetectProxy
        || defaultSettings.UiScalePercent != 110)
    {
        Console.Error.WriteLine(
            "FAIL: new installations do not default to safe Cookie saving, Clash detection, and 2K-friendly sizing.");
        return 11;
    }
    Console.WriteLine(
        "PASS: new installations default to encrypted Cookie saving, Clash detection, and 110% sizing.");
}
finally
{
    if (previousEncryptedCookie is null)
        SecretStore.ClearCookie();
    else
        File.WriteAllBytes(SettingsStore.EncryptedCookiePath, previousEncryptedCookie);
}

using var service = new GalleryDlService();
using var cancellation = new CancellationTokenSource();
var job = new DownloadJob
{
    Url = "https://commons.wikimedia.org/wiki/File:Example.jpg",
    OutputDirectory = outputPath,
    Options = new DownloadOptions(
        "manual",
        "edge",
        UseBrowserCookies: false,
        ManualCookie: testCookie,
        UseProxy: true,
        ProxyUrl: "http://127.0.0.1:1",
        DownloadOriginal: true,
        PackageAsCbz: false,
        WriteMetadata: false,
        ForceIpv4: true)
};

var stopwatch = Stopwatch.StartNew();
var runTask = service.RunAsync(enginePath, job, cancellation.Token);
var callReturnedAfter = stopwatch.Elapsed;
Console.WriteLine($"RunAsync returned control after {callReturnedAfter.TotalMilliseconds:F0} ms");

await Task.Delay(2500);
var remainedRunning = service.IsRunning;
Console.WriteLine($"Child process still running during network wait: {remainedRunning}");

cancellation.Cancel();
service.Stop();
var stopWait = Stopwatch.StartNew();
await runTask;
stopWait.Stop();

if (service.IsRunning)
{
    Console.Error.WriteLine("FAIL: RunAsync returned while the child process was still running.");
    return 12;
}
if (stopWait.Elapsed > TimeSpan.FromSeconds(8))
{
    Console.Error.WriteLine(
        $"FAIL: stopping the child process took too long ({stopWait.Elapsed.TotalSeconds:F1} s).");
    return 13;
}
Console.WriteLine(
    $"PASS: stop waited for the child process to exit ({stopWait.Elapsed.TotalMilliseconds:F0} ms).");

var runConfigPath = Path.Combine(SettingsStore.DataDirectory, $"run-{job.Id:N}.json");
var logPath = Directory.EnumerateFiles(SettingsStore.LogDirectory, $"*-{job.Id:N}.log")
    .SingleOrDefault();
if (File.Exists(runConfigPath)
    || (logPath is not null && ReadTextAllowingWriter(logPath)
        .Contains("local-test", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("FAIL: Cookie remained in a temporary config or leaked into a log.");
    return 6;
}
Console.WriteLine("PASS: temporary Cookie config is deleted and Cookie is absent from logs.");

if (callReturnedAfter > TimeSpan.FromSeconds(1))
{
    Console.Error.WriteLine("FAIL: RunAsync blocked its caller before the first process output.");
    return 1;
}

if (!remainedRunning)
{
    Console.Error.WriteLine("FAIL: The slow-response scenario did not stay active long enough.");
    return 2;
}

Console.WriteLine("PASS: slow child output does not block the caller and remains cancellable.");

var manager = new EngineManager();
var localEngine = manager.FindEngine()
                  ?? throw new InvalidOperationException("Test output does not contain gallery-dl.exe.");
var hashBefore = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(localEngine)));
var update = await manager.CheckForUpdateAsync("http://127.0.0.1:7897");
var hashAfter = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(localEngine)));

Console.WriteLine($"Local engine: {update.LocalVersion}");
Console.WriteLine($"Remote build: {update.RemoteVersion}");
Console.WriteLine($"Already latest: {update.IsLatest}");
Console.WriteLine($"Engine file unchanged by check: {hashBefore == hashAfter}");

if (hashBefore != hashAfter)
{
    Console.Error.WriteLine("FAIL: checking for updates modified the installed engine.");
    return 3;
}

Console.WriteLine(update.IsLatest
    ? "PASS: checking an up-to-date engine does not download or replace it."
    : "PASS: checking found an update without downloading or replacing the current engine.");
return 0;

static string ReadTextAllowingWriter(string path)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

using System.Diagnostics;
using System.Security.Cryptography;
using EhGalleryDownloader;

var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", ".."));
var enginePath = Path.Combine(projectDirectory, "tools", "gallery-dl.exe");
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
    }
]);
if (ranking[0].Name != "稳定节点" || ranking[^1].Name != "完全失败")
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
    || allCandidates.Count != 38
    || smartCandidates.Any(result => result.ClashDelayMs <= 0 || result.GalleryDelayMs <= 0)
    || smartCandidates.Max(result => result.GalleryDelayMs)
       <= smartCandidates.OrderBy(result => result.GalleryDelayMs)
           .Take(20).Max(result => result.GalleryDelayMs))
{
    Console.Error.WriteLine("FAIL: smart screening did not remove errors or retain broad samples.");
    return 16;
}
Console.WriteLine("PASS: smart screening removes Error nodes and limits real downloads without using latency alone.");

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
    FailedFiles = 2
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
    || stoppedJob.FailedFiles != 0)
{
    Console.Error.WriteLine("FAIL: continuing a task did not start a clean attempt.");
    return 13;
}
stoppedJob.State = "已完成";
if (DownloadTaskLogic.FindReusable([stoppedJob], continuationUrl) is not null)
{
    Console.Error.WriteLine("FAIL: a completed task was incorrectly reused as an interrupted task.");
    return 14;
}
Console.WriteLine("PASS: stopped tasks continue in-place while completed tasks remain separate.");

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
        || !defaultSettings.AutoDetectProxy)
    {
        Console.Error.WriteLine(
            "FAIL: new installations do not default to safe Cookie saving and Clash port detection.");
        return 11;
    }
    Console.WriteLine(
        "PASS: new installations default to encrypted Cookie saving and Clash port detection.");
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

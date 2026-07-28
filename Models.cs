using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EhGalleryDownloader;

public sealed class DownloadJob : INotifyPropertyChanged
{
    private string _state = "等待";
    private string _currentFile = "—";
    private int _completedFiles;
    private int _skippedFiles;
    private int _failedFiles;
    private string _details = "等待开始。";
    private DateTime? _finishedAt;
    private DateTime _attemptStartedAt = DateTime.Now;
    private int _attemptCount;

    public Guid Id { get; } = Guid.NewGuid();
    public DateTime CreatedAt { get; } = DateTime.Now;
    public string CreatedAtText => CreatedAt.ToString("HH:mm:ss");
    public required string Url { get; init; }
    public required string OutputDirectory { get; set; }
    public required DownloadOptions Options { get; set; }
    public string? BlockedEngineVersion { get; set; }

    public string State { get => _state; set => Set(ref _state, value); }
    public string CurrentFile { get => _currentFile; set => Set(ref _currentFile, value); }
    public int CompletedFiles { get => _completedFiles; set { if (Set(ref _completedFiles, value)) OnPropertyChanged(nameof(ProgressText)); } }
    public int SkippedFiles { get => _skippedFiles; set { if (Set(ref _skippedFiles, value)) OnPropertyChanged(nameof(ProgressText)); } }
    public int FailedFiles { get => _failedFiles; set { if (Set(ref _failedFiles, value)) OnPropertyChanged(nameof(ProgressText)); } }
    public string Details { get => _details; set => Set(ref _details, value); }
    public DateTime? FinishedAt { get => _finishedAt; set { if (Set(ref _finishedAt, value)) OnPropertyChanged(nameof(DurationText)); } }
    public int AttemptCount { get => _attemptCount; private set => Set(ref _attemptCount, value); }

    public string ProgressText => CompletedFiles == 0 && SkippedFiles == 0 && FailedFiles == 0
        ? "—"
        : $"{CompletedFiles} 完成 / {SkippedFiles} 已有 / {FailedFiles} 失败";

    public string DurationText
    {
        get
        {
            var end = FinishedAt ?? DateTime.Now;
            var span = end - _attemptStartedAt;
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }
    }

    public void BeginAttempt()
    {
        AttemptCount++;
        _attemptStartedAt = DateTime.Now;
        FinishedAt = null;
        CompletedFiles = 0;
        SkippedFiles = 0;
        FailedFiles = 0;
        CurrentFile = "—";
        OnPropertyChanged(nameof(DurationText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record DownloadOptions(
    string LoginMode,
    string Browser,
    bool UseBrowserCookies,
    string? ManualCookie,
    bool UseProxy,
    string ProxyUrl,
    bool DownloadOriginal,
    bool PackageAsCbz,
    bool WriteMetadata,
    bool ForceIpv4);

public sealed class AppSettings
{
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "画廊下载");
    public string Browser { get; set; } = "edge";
    public string LoginMode { get; set; } = "manual";
    public bool UseBrowserCookies { get; set; }
    public bool RememberCookie { get; set; } = true;
    public bool CookieStoragePreferenceSet { get; set; }
    public bool UseProxy { get; set; } = true;
    public bool AutoDetectProxy { get; set; } = true;
    public string ProxyUrl { get; set; } = "http://127.0.0.1:7897";
    public bool DownloadOriginal { get; set; } = true;
    public bool PackageAsCbz { get; set; }
    public bool WriteMetadata { get; set; } = true;
    public bool ForceIpv4 { get; set; } = true;
}

public sealed class ProxyInfo
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Now { get; init; } = "";
    public IReadOnlyList<string> All { get; init; } = Array.Empty<string>();
    public bool IsGroup => All.Count > 0;
}

public sealed class ClashState
{
    public required IReadOnlyDictionary<string, ProxyInfo> Proxies { get; init; }
    public int MixedPort { get; init; }
    public string ProxyUrl { get; init; } = "";
    public string ProxyPortKind { get; init; } = "";
    public string Mode { get; init; } = "";
}

public sealed class NodeProbeResult : INotifyPropertyChanged
{
    private int _rank;
    private string _status = "等待";
    private int _successfulSamples;
    private int _sampleCount;
    private int _interruptions;
    private double _speedMbPerSecond;
    private double _downloadedMb;
    private int _clashDelayMs;
    private int _galleryDelayMs;
    private bool _clashScreened;
    private bool _galleryScreened;
    private bool _eligibleForRealTest;
    private string _rating = "尚未测试";
    private string _details = "尚未开始测试。";

    public required string Name { get; init; }
    public required string Type { get; init; }
    public int Rank { get => _rank; set { if (Set(ref _rank, value)) OnPropertyChanged(nameof(RankText)); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int SuccessfulSamples { get => _successfulSamples; set { if (Set(ref _successfulSamples, value)) OnPropertyChanged(nameof(SuccessText)); } }
    public int SampleCount { get => _sampleCount; set { if (Set(ref _sampleCount, value)) OnPropertyChanged(nameof(SuccessText)); } }
    public int Interruptions { get => _interruptions; set => Set(ref _interruptions, value); }
    public double SpeedMbPerSecond { get => _speedMbPerSecond; set { if (Set(ref _speedMbPerSecond, value)) OnPropertyChanged(nameof(SpeedText)); } }
    public double DownloadedMb { get => _downloadedMb; set { if (Set(ref _downloadedMb, value)) OnPropertyChanged(nameof(DownloadedText)); } }
    public int ClashDelayMs { get => _clashDelayMs; set { if (Set(ref _clashDelayMs, value)) OnPropertyChanged(nameof(ClashDelayText)); } }
    public int GalleryDelayMs { get => _galleryDelayMs; set { if (Set(ref _galleryDelayMs, value)) OnPropertyChanged(nameof(GalleryDelayText)); } }
    public bool ClashScreened { get => _clashScreened; set { if (Set(ref _clashScreened, value)) OnPropertyChanged(nameof(ClashDelayText)); } }
    public bool GalleryScreened { get => _galleryScreened; set { if (Set(ref _galleryScreened, value)) OnPropertyChanged(nameof(GalleryDelayText)); } }
    public bool EligibleForRealTest { get => _eligibleForRealTest; set => Set(ref _eligibleForRealTest, value); }
    public string Rating { get => _rating; set => Set(ref _rating, value); }
    public string Details { get => _details; set => Set(ref _details, value); }

    public string RankText => Rank <= 0 ? "—" : Rank.ToString();
    public string SuccessText => SampleCount <= 0 ? "—" : $"{SuccessfulSamples}/{SampleCount}";
    public string SpeedText => SpeedMbPerSecond <= 0 ? "—" : $"{SpeedMbPerSecond:F2} MB/s";
    public string DownloadedText => DownloadedMb <= 0 ? "—" : $"{DownloadedMb:F1} MB";
    public string ClashDelayText => !ClashScreened
        ? "—"
        : ClashDelayMs > 0 ? $"{ClashDelayMs} ms" : "Error";
    public string GalleryDelayText => !GalleryScreened
        ? "—"
        : GalleryDelayMs > 0 ? $"{GalleryDelayMs} ms" : "Error";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record NodeProbeMeasurement(
    int SuccessfulSamples,
    int SampleCount,
    int Interruptions,
    double SpeedMbPerSecond,
    double DownloadedMb,
    string Details);

public sealed record NodeTestRecoveryState(
    string Endpoint,
    string Group,
    string Node,
    string Mode = "");

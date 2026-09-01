using System.Diagnostics;

namespace EhGalleryDownloader;

public sealed class DownloadProgressTracker
{
    private readonly Stopwatch _stopwatch = new();

    public bool IsTransferStarted => _stopwatch.IsRunning;

    public void Reset() => _stopwatch.Reset();

    public DownloadProgress Record(
        int current,
        int total,
        long downloadedBytes,
        int processedFiles)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();
        return DownloadProgressCalculator.Calculate(
            current,
            total,
            downloadedBytes,
            _stopwatch.Elapsed.TotalSeconds,
            processedFiles);
    }
}

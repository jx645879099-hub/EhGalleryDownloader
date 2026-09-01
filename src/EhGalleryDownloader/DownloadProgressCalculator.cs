namespace EhGalleryDownloader;

public static class DownloadProgressCalculator
{
    public static DownloadProgress Calculate(
        int current,
        int total,
        long downloadedBytes,
        double elapsedSeconds,
        int processedFiles)
    {
        var speed = elapsedSeconds > 0
            ? downloadedBytes / 1024d / 1024d / elapsedSeconds
            : 0;
        TimeSpan? remaining = null;
        if (processedFiles > 0 && total > current && elapsedSeconds > 0)
        {
            remaining = TimeSpan.FromSeconds(
                elapsedSeconds / processedFiles * Math.Max(total - current, 0));
        }
        return new DownloadProgress(current, total, speed, remaining);
    }
}

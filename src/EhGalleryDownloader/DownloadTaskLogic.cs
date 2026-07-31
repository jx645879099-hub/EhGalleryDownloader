namespace EhGalleryDownloader;

public static class DownloadTaskLogic
{
    public static DownloadJob? FindReusable(
        IEnumerable<DownloadJob> jobs,
        string url)
    {
        var normalized = NormalizeGalleryUrl(url);
        return jobs.FirstOrDefault(job =>
            job.State != "已完成"
            && NormalizeGalleryUrl(job.Url).Equals(
                normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeGalleryUrl(string url) =>
        url.Trim().TrimEnd('/');
}

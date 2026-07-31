using System.Text.Json;

namespace EhGalleryDownloader;

public static class JobStore
{
    private sealed record StoredJob(
        Guid Id,
        string Url,
        string OutputDirectory,
        DownloadOptions Options,
        string State,
        int AttemptCount,
        DateTime CreatedAt,
        string? GalleryTitle,
        int CompletedFiles,
        int SkippedFiles,
        int FailedFiles,
        int TotalFiles,
        DateTime? FinishedAt);

    public static IReadOnlyList<DownloadJob> Load() => LoadFromPath(SettingsStore.JobsPath);

    public static IReadOnlyList<DownloadJob> LoadFromPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var stored = JsonSerializer.Deserialize<List<StoredJob>>(
                File.ReadAllText(path)) ?? [];
            return stored.Select(item => new DownloadJob
            {
                Id = item.Id,
                CreatedAt = item.CreatedAt,
                Url = item.Url,
                OutputDirectory = item.OutputDirectory,
                Options = item.Options with { ManualCookie = null },
                State = item.State is "下载中" or "排队中" ? "已停止" : item.State,
                Details = item.State is "下载中" or "排队中"
                    ? "上次运行在任务完成前结束，可以继续补漏。"
                    : "已从上次运行恢复。",
                AttemptCount = item.AttemptCount,
                GalleryTitle = item.GalleryTitle ?? "",
                CompletedFiles = item.CompletedFiles,
                SkippedFiles = item.SkippedFiles,
                FailedFiles = item.FailedFiles,
                TotalFiles = item.TotalFiles,
                FinishedAt = item.FinishedAt
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<DownloadJob> jobs) =>
        SaveToPath(SettingsStore.JobsPath, jobs);

    public static void SaveToPath(string path, IEnumerable<DownloadJob> jobs)
    {
        var stored = jobs.Take(200).Select(job => new StoredJob(
            job.Id,
            job.Url,
            job.OutputDirectory,
            job.Options with { ManualCookie = null },
            job.State,
            job.AttemptCount,
            job.CreatedAt,
            job.GalleryTitle,
            job.CompletedFiles,
            job.SkippedFiles,
            job.FailedFiles,
            job.TotalFiles,
            job.FinishedAt)).ToList();
        SettingsStore.WriteAllTextAtomic(
            path,
            JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }
}

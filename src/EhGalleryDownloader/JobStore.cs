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
        DateTime? FinishedAt,
        string? Details = null,
        string? BlockedEngineVersion = null,
        DateTime? AttemptStartedAt = null);

    public static IReadOnlyList<DownloadJob> Load() => LoadFromPath(SettingsStore.JobsPath);

    public static IReadOnlyList<DownloadJob> LoadFromPath(string path)
    {
        Exception? lastError = null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var stored = JsonSerializer.Deserialize<List<StoredJob>>(
                    File.ReadAllText(candidate)) ?? throw new JsonException("任务记录为空。");
                return stored.Select(item => new DownloadJob
                {
                    Id = item.Id,
                    CreatedAt = item.CreatedAt,
                    Url = item.Url,
                    OutputDirectory = item.OutputDirectory,
                    Options = item.Options with { ManualCookie = null },
                    State = item.State is "下载中" or "排队中" or "自动续传" or "准备中" ? "已停止" : item.State,
                    Details = item.State is "下载中" or "排队中" or "自动续传" or "准备中"
                        ? "上次运行在任务完成前结束，可以继续补漏。"
                        : item.Details ?? (item.State == "失败"
                            ? "上次下载未完成。点击“继续下载”可接着处理，已有图片会保留。"
                            : "已从上次运行恢复。"),
                    BlockedEngineVersion = item.BlockedEngineVersion,
                    AttemptStartedAt = item.AttemptStartedAt ?? item.CreatedAt,
                    AttemptCount = item.AttemptCount,
                    GalleryTitle = item.GalleryTitle ?? "",
                    CompletedFiles = item.CompletedFiles,
                    SkippedFiles = item.SkippedFiles,
                    FailedFiles = item.FailedFiles,
                    TotalFiles = item.TotalFiles,
                    FinishedAt = item.FinishedAt
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
            {
                lastError = ex;
            }
        }
        if (lastError is not null)
            throw new InvalidDataException("下载记录和备份均无法读取，已保留原文件。", lastError);
        return [];
    }

    public static void Save(IEnumerable<DownloadJob> jobs) =>
        SaveToPath(SettingsStore.JobsPath, jobs);

    public static void SaveToPath(string path, IEnumerable<DownloadJob> jobs)
    {
        var stored = jobs.Select(job => new StoredJob(
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
            job.FinishedAt,
            job.Details,
            job.BlockedEngineVersion,
            job.AttemptStartedAt)).ToList();
        // Never replace a good backup with a damaged primary file.
        if (File.Exists(path))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<List<StoredJob>>(File.ReadAllText(path));
                if (previous is not null) File.Copy(path, path + ".bak", overwrite: true);
            }
            catch (JsonException) { }
        }
        SettingsStore.WriteAllTextAtomic(
            path,
            JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }
}

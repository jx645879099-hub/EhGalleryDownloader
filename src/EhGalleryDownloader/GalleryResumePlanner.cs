using System.Text.RegularExpressions;

namespace EhGalleryDownloader;

public sealed record GalleryResumePlan(
    int StartIndex,
    int ExistingPrefixCount,
    string InputUrl,
    string? Range);

public static partial class GalleryResumePlanner
{
    private static readonly HashSet<string> ImageExtensions = new(
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif"],
        StringComparer.OrdinalIgnoreCase);

    public static GalleryResumePlan? Create(DownloadJob job)
    {
        if (job.Options.PackageAsCbz
            || !Directory.Exists(job.OutputDirectory)
            || !TryGetGalleryId(job.Url, out var galleryId))
            return null;

        var existing = new HashSet<int>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         job.OutputDirectory,
                         $"{galleryId}_*",
                         SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(path);
                if (!ImageExtensions.Contains(extension)) continue;

                FileInfo file;
                try { file = new FileInfo(path); }
                catch { continue; }
                if (file.Length <= 0) continue;

                var match = NumberedFileRegex().Match(file.Name);
                if (!match.Success
                    || !match.Groups["gid"].Value.Equals(
                        galleryId, StringComparison.Ordinal)
                    || !int.TryParse(match.Groups["index"].Value, out var index)
                    || index <= 0)
                    continue;
                existing.Add(index);
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var firstMissing = 1;
        while (existing.Contains(firstMissing)) firstMissing++;
        if (firstMissing <= 1) return null;

        var inputUrl = job.Url;
        string? range = $"{firstMissing}-";
        if (TryBuildContinuationUrl(job.Url, firstMissing, out var continuationUrl))
        {
            inputUrl = continuationUrl;
            range = null;
        }
        return new GalleryResumePlan(firstMissing, firstMissing - 1, inputUrl, range);
    }

    private static bool TryBuildContinuationUrl(
        string url,
        int startIndex,
        out string continuationUrl)
    {
        continuationUrl = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        try
        {
            var builder = new UriBuilder(uri) { Fragment = $"page{startIndex}" };
            continuationUrl = builder.Uri.AbsoluteUri;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool TryGetGalleryId(string url, out string galleryId)
    {
        galleryId = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.FindIndex(parts,
            part => part.Equals("g", StringComparison.OrdinalIgnoreCase));
        if (marker < 0 || marker + 1 >= parts.Length) return false;
        galleryId = parts[marker + 1];
        return galleryId.All(char.IsDigit);
    }

    [GeneratedRegex("^(?<gid>\\d+)_(?<index>\\d+)_", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedFileRegex();
}

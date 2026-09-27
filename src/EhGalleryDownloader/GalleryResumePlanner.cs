using System.Text.RegularExpressions;

namespace EhGalleryDownloader;

public sealed record GalleryResumePlan(
    int StartIndex,
    int ExistingPrefixCount,
    string InputUrl,
    string? Range,
    bool AlreadyComplete = false,
    int AnchorIndex = 0);

public static partial class GalleryResumePlanner
{
    public static bool IsImageAnchorUnavailable(string rawError) =>
        rawError.Contains("NotFoundError: Requested image page could not be found",
            StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> ImageExtensions = new(
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif"],
        StringComparer.OrdinalIgnoreCase);

    public static GalleryResumePlan? Create(
        DownloadJob job,
        bool compatibilityMode = false,
        CancellationToken cancellationToken = default)
    {
        if (job.Options.PackageAsCbz
            || !Directory.Exists(job.OutputDirectory)
            || !TryGetGalleryId(job.Url, out var galleryId))
            return null;

        var existing = new HashSet<int>();
        var imageTokens = new Dictionary<int, string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         job.OutputDirectory,
                         $"{galleryId}_*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                var tokenMatch = ImageTokenRegex().Match(file.Name);
                if (tokenMatch.Success)
                    imageTokens.TryAdd(index, tokenMatch.Groups["token"].Value);
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var firstMissing = 1;
        while (existing.Contains(firstMissing)) firstMissing++;
        if (firstMissing <= 1) return null;

        if (job.TotalFiles > 0 && firstMissing > job.TotalFiles)
            return new GalleryResumePlan(
                firstMissing, firstMissing - 1, job.Url, null, AlreadyComplete: true);

        var inputUrl = job.Url;
        string? range = $"{firstMissing}-";
        var anchorIndex = firstMissing - 1;
        if (!compatibilityMode
            && imageTokens.TryGetValue(anchorIndex, out var imageToken)
            && TryBuildContinuationUrl(
                job.Url, galleryId, anchorIndex, imageToken, out var continuationUrl))
        {
            inputUrl = continuationUrl;
            range = null;
            return new GalleryResumePlan(
                firstMissing, anchorIndex, inputUrl, range, AnchorIndex: anchorIndex);
        }
        return new GalleryResumePlan(firstMissing, firstMissing - 1, inputUrl, range);
    }

    private static bool TryBuildContinuationUrl(
        string url,
        string galleryId,
        int anchorIndex,
        string imageToken,
        out string continuationUrl)
    {
        continuationUrl = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        try
        {
            var builder = new UriBuilder(uri)
            {
                Path = $"/s/{imageToken}/{galleryId}-{anchorIndex}",
                Query = "",
                Fragment = ""
            };
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

    [GeneratedRegex("^\\d+_\\d+_(?<token>[0-9a-fA-F]{10})_", RegexOptions.CultureInvariant)]
    private static partial Regex ImageTokenRegex();
}

namespace EhGalleryDownloader;

public enum GalleryDlEventKind
{
    Metadata,
    Prepare,
    Success,
    Skip,
    Failure
}

public sealed record GalleryDlEvent(
    GalleryDlEventKind Kind,
    int Current,
    int Total,
    string Path);

public static class GalleryDlOutputParser
{
    private static readonly (string Prefix, GalleryDlEventKind Kind)[] Markers =
    [
        ("__GUI_META__|", GalleryDlEventKind.Metadata),
        ("__GUI_PREPARE__|", GalleryDlEventKind.Prepare),
        ("__GUI_SUCCESS__|", GalleryDlEventKind.Success),
        ("__GUI_SKIP__|", GalleryDlEventKind.Skip),
        ("__GUI_FAILURE__|", GalleryDlEventKind.Failure)
    ];

    public static bool TryParse(string line, out GalleryDlEvent result)
    {
        foreach (var marker in Markers)
        {
            if (!line.StartsWith(marker.Prefix, StringComparison.Ordinal)) continue;
            var payload = line[marker.Prefix.Length..];
            var parts = payload.Split('|', 3);
            result = parts.Length == 3
                ? new GalleryDlEvent(
                    marker.Kind,
                    int.TryParse(parts[0], out var current) ? current : 0,
                    int.TryParse(parts[1], out var total) ? total : 0,
                    parts[2].Trim())
                : new GalleryDlEvent(marker.Kind, 0, 0, payload.Trim());
            return true;
        }
        result = null!;
        return false;
    }
}

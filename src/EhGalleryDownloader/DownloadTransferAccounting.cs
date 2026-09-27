namespace EhGalleryDownloader;

public sealed class DownloadTransferAccounting
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _initialPartBytes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GalleryDlEventKind> _outcomes =
        new(StringComparer.OrdinalIgnoreCase);
    private long _downloadedBytes;

    public long DownloadedBytes
    {
        get { lock (_gate) return _downloadedBytes; }
    }

    public int ProcessedFiles
    {
        get { lock (_gate) return _outcomes.Count; }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _initialPartBytes.Clear();
            _outcomes.Clear();
            _downloadedBytes = 0;
        }
    }

    public void RecordPrepare(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            if (!_initialPartBytes.ContainsKey(path))
                _initialPartBytes[path] = FileLength(path + ".part");
        }
    }

    public bool TryRecordTerminal(
        GalleryDlEventKind kind,
        string path,
        out GalleryDlEventKind? previous)
    {
        previous = null;
        if (string.IsNullOrWhiteSpace(path)
            || kind is not (GalleryDlEventKind.Success
                or GalleryDlEventKind.Skip
                or GalleryDlEventKind.Failure))
            return false;

        lock (_gate)
        {
            if (_outcomes.TryGetValue(path, out var old))
            {
                if (Priority(kind) <= Priority(old)) return false;
                previous = old;
            }

            _outcomes[path] = kind;
            if (kind == GalleryDlEventKind.Success)
            {
                _initialPartBytes.TryGetValue(path, out var existingBytes);
                _downloadedBytes += Math.Max(0, FileLength(path) - existingBytes);
            }
            return true;
        }
    }

    private static int Priority(GalleryDlEventKind kind) => kind switch
    {
        GalleryDlEventKind.Success => 3,
        GalleryDlEventKind.Skip => 2,
        GalleryDlEventKind.Failure => 1,
        _ => 0
    };

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (ArgumentException) { return 0; }
    }
}

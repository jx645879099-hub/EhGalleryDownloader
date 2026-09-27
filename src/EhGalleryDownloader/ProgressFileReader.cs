using System.Text;

namespace EhGalleryDownloader;

// Keep incomplete UTF-8 characters and lines for the next poll.
public sealed class ProgressFileReader
{
    private long _position;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _pending = "";

    public IReadOnlyList<string> ReadAvailable(string path, bool final = false)
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                _pending = "";
                _decoder.Reset();
            }
            stream.Position = _position;
            var buffer = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                var charCount = _decoder.GetChars(buffer, 0, count, chars, 0, flush: false);
                _pending += new string(chars, 0, charCount);
                _position += count;
                int newline;
                while ((newline = _pending.IndexOf('\n')) >= 0)
                {
                    lines.Add(_pending[..newline].TrimEnd('\r'));
                    _pending = _pending[(newline + 1)..];
                }
            }
            if (final && _pending.Length > 0)
            {
                lines.Add(_pending.TrimEnd('\r'));
                _pending = "";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return lines;
    }
}

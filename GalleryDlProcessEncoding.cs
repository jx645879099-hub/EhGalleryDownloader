using System.Text;

namespace EhGalleryDownloader;

public static class GalleryDlProcessEncoding
{
    private static readonly Lazy<Encoding> Resolved = new(Resolve);

    public static Encoding Current => Resolved.Value;

    private static Encoding Resolve() => new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);
}

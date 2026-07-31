namespace EhGalleryDownloader;

public static class DownloadErrorClassifier
{
    private static readonly string[] TransientTerms =
    [
        "timeout", "timed out", "connection", "proxy", "ssl", "eof",
        "429", "503", "502", "network", "reset by peer", "temporarily unavailable"
    ];

    public static bool IsTransient(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && TransientTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    public static bool IsAuthenticationFailure(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("AuthenticationError", StringComparison.OrdinalIgnoreCase)
            || text.Contains("requires you to log on", StringComparison.OrdinalIgnoreCase));
}

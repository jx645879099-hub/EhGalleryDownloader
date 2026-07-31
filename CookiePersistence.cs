namespace EhGalleryDownloader;

public static class CookiePersistence
{
    public static bool SaveIfEnabled(string? cookie, bool enabled)
    {
        if (!enabled
            || !CookieParser.TryParse(cookie, out _, out _))
            return false;

        SecretStore.SaveCookie(cookie!.Trim());
        return true;
    }
}

using System.Text.Json;

namespace EhGalleryDownloader;

public static class SettingsStore
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EhGalleryDownloader");

    public static string ArchivePath => Path.Combine(DataDirectory, "download-archive.sqlite3");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string EncryptedCookiePath => Path.Combine(DataDirectory, "login-cookie.dat");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            // 损坏的设置不会阻止软件启动。
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void CleanupStaleRunFiles()
    {
        try
        {
            if (!Directory.Exists(DataDirectory)) return;
            foreach (var path in Directory.EnumerateFiles(DataDirectory, "*.json")
                         .Where(path =>
                             Path.GetFileName(path).StartsWith("run-", StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileName(path).StartsWith("probe-", StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch { }
    }
}

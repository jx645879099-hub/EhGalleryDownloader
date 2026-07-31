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
    public static string JobsPath => Path.Combine(DataDirectory, "jobs.json");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                               ?? new AppSettings();
                if (settings.UiScalePercent is not (100 or 110 or 125))
                    settings.UiScalePercent = 110;
                return settings;
            }
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
        WriteAllTextAtomic(
            SettingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void WriteAllTextAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, true);
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

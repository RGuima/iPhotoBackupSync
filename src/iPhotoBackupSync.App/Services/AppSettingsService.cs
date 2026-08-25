using System.IO;
using System.Text.Json;

namespace iPhotoBackupSync.App.Services;

public sealed class AppSettings
{
    public string? OriginPath { get; set; }
    public string? DestinationPath { get; set; }
}

/// <summary>
/// Persists the last-used origin/destination folders across sessions as a small
/// JSON file under %AppData%. Best-effort: any failure to read or write is
/// swallowed since this is a convenience, not critical state.
/// </summary>
public static class AppSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "iPhotoBackupSync", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence only.
        }
    }
}

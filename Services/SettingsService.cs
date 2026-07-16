using System.IO;
using System.Text.Json;

namespace PakeStudio.Services;

public sealed class AppSettings
{
    public string LastUrl { get; set; } = "";
    public string LastAppName { get; set; } = "";
    public string LastIconUrl { get; set; } = "";
    public string Width { get; set; } = "1200";
    public string Height { get; set; } = "780";
    public string Version { get; set; } = "1.0.0";
    public string Architecture { get; set; } = "x64";
    public bool Fullscreen { get; set; }
    public string LastArtifactPath { get; set; } = "";
}

public static class SettingsService
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PakeStudio");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}

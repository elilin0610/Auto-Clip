using System.IO;
using System.Text.Json;

namespace ApexHighlighter.App;

/// <summary>Per-user settings, stored in %LOCALAPPDATA%\ApexHighlighter\settings.json.</summary>
public sealed class AppSettings
{
    public string? UiLanguage { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexHighlighter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* A broken settings file must never stop the app from starting. */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Not worth interrupting the user over. */ }
    }
}

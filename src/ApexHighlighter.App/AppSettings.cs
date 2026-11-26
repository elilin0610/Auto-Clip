using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

// =====================================================================================
// AppSettings.cs — 使用者設定的讀寫
//
// 存在 %LOCALAPPDATA%\ApexHighlighter\settings.json：
// 介面語言、輸出資料夾、匯出格式、視窗位置大小、OBS 連線設定。
// OBS 密碼用 Windows DPAPI 加密後才存，只有同一個 Windows 帳戶能解開，檔案裡沒有明碼。
// =====================================================================================

/// <summary>Per-user settings, stored in %LOCALAPPDATA%\ApexHighlighter\settings.json.</summary>
public sealed class AppSettings
{
    public string? UiLanguage { get; set; }

    // Remembered between runs.
    public string? OutputDir { get; set; }
    public ExportFormat ExportFormat { get; set; } = ExportFormat.Landscape;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // OBS (obs-websocket 5).
    public bool ObsEnabled { get; set; }
    public string ObsHost { get; set; } = "localhost";
    public int ObsPort { get; set; } = 4455;
    /// <summary>The OBS password, encrypted with Windows DPAPI for the current user (never stored as plain text).</summary>
    public string? ObsPasswordProtected { get; set; }

    private static readonly byte[] Entropy = "ApexHighlighter.obs"u8.ToArray();

    public string? GetObsPassword()
    {
        if (string.IsNullOrEmpty(ObsPasswordProtected)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(ObsPasswordProtected), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }   // Saved by another Windows account or corrupted: treat as no password.
    }

    public void SetObsPassword(string? password)
    {
        ObsPasswordProtected = string.IsNullOrEmpty(password) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));
    }

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

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexHighlighter.Core;

// =====================================================================================
// ScanCache.cs — 記住每支錄影的掃描結果
//
// 掃完一支錄影，就把結果存到 %LOCALAPPDATA%\ApexHighlighter\scans\cache\。
// 下次再打開同一支錄影（同路徑、同大小、同修改時間），直接讀回上次的結果，不用重掃（1 小時錄影約省 4 分鐘）。
// 錄影被改過（大小或修改時間不同）就當作沒有快取。想重掃按「掃描精華」即可。
// =====================================================================================

public static class ScanCache
{
    /// <summary>Bump when the saved format or the detection rules change enough that old results should be ignored.</summary>
    public const int Version = 1;

    public static string DefaultFolder => Path.Combine(ScanReport.DefaultFolder, "cache");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Entry
    {
        public int Version { get; set; }
        public string Source { get; set; } = "";
        public long Size { get; set; }
        public long WriteTimeUtcTicks { get; set; }
        public DateTime SavedAt { get; set; }
        public ScanResult? Result { get; set; }
    }

    public static string KeyFor(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24];
    }

    /// <summary>Saves the result for this recording. Never throws (a cache is not worth failing a scan over).</summary>
    public static string? Save(ScanResult result, string? folder = null)
    {
        try
        {
            folder ??= DefaultFolder;
            Directory.CreateDirectory(folder);
            var fi = new FileInfo(result.File);
            var e = new Entry
            {
                Version = Version, Source = fi.FullName, Size = fi.Length, WriteTimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                SavedAt = DateTime.Now, Result = result,
            };
            var file = Path.Combine(folder, KeyFor(result.File) + ".json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(e, Json), Encoding.UTF8);
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch { return null; }
    }

    /// <summary>The last scan of this exact file (same path, size and modification time), or null.</summary>
    public static (ScanResult Result, DateTime SavedAt)? TryLoad(string path, string? folder = null)
    {
        try
        {
            folder ??= DefaultFolder;
            var file = Path.Combine(folder, KeyFor(path) + ".json");
            if (!File.Exists(file)) return null;
            var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file), Json);
            var fi = new FileInfo(path);
            if (e?.Result == null || e.Version != Version || !fi.Exists || e.Size != fi.Length || e.WriteTimeUtcTicks != fi.LastWriteTimeUtc.Ticks)
                return null;
            return (e.Result, e.SavedAt);
        }
        catch { return null; }
    }
}

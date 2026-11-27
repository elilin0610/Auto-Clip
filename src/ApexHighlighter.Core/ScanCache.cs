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
// 也會記：
//   ・掃描時的設定（段數、門檻、最長秒數、二次確認參數、關鍵字表）的指紋 → 之後改了設定，讀回時會提醒「要重掃才會套用」
//   ・使用者在介面上的微調（起訖、勾選、手動新增的片段）→ 重開錄影時照原樣還原
// =====================================================================================

public static class ScanCache
{
    /// <summary>Bump when the saved format or the detection rules change enough that old results should be ignored.</summary>
    public const int Version = 2;   // 2: second look (CandidateVerifier), settings fingerprint, saved clip edits

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
        public string? Settings { get; set; }
        public List<SavedClip>? Clips { get; set; }
    }

    /// <summary>
    /// Short fingerprint of everything that changes which clips a scan suggests: selection settings, second-look settings
    /// and the keyword rules. A cached scan with a different fingerprint is still shown, but flagged so the user can rescan.
    /// </summary>
    public static string Fingerprint(ScanOptions o, KeywordConfig? cfg)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var s = string.Join("|",
            o.SampleFps.ToString(inv), o.MergeGap.ToString(inv), o.MinPreRoll.ToString(inv), o.MaxPreRoll.ToString(inv),
            o.PostRoll.ToString(inv), o.MinScore.ToString(inv), o.MaxCandidates.ToString(inv), o.MaxClipLength.ToString(inv),
            o.VerifyCandidates, o.VerifyFps.ToString(inv), o.VerifyTrustSamples.ToString(inv), o.VerifyMinFrames.ToString(inv),
            o.VerifySpare.ToString(inv), cfg == null ? "" : JsonSerializer.Serialize(cfg, Json));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }

    public static string KeyFor(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24];
    }

    /// <summary>
    /// Saves a fresh scan of this recording (any clip edits saved for an older scan are dropped).
    /// Never throws (a cache is not worth failing a scan over).
    /// </summary>
    public static string? Save(ScanResult result, string? settings = null, string? folder = null)
    {
        try
        {
            folder ??= DefaultFolder;
            Directory.CreateDirectory(folder);
            var fi = new FileInfo(result.File);
            var e = new Entry
            {
                Version = Version, Source = fi.FullName, Size = fi.Length, WriteTimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                SavedAt = DateTime.Now, Result = result, Settings = settings,
            };
            return Write(e, folder);
        }
        catch { return null; }
    }

    /// <summary>
    /// Remembers the user's clip list (times, ticks, manual clips) for a recording whose scan is cached.
    /// Returns false when there is no valid cached scan to attach it to. Never throws.
    /// </summary>
    public static bool SaveClips(string path, IReadOnlyList<SavedClip> clips, string? folder = null)
    {
        try
        {
            folder ??= DefaultFolder;
            var e = ReadValid(path, folder);
            if (e == null) return false;
            e.Clips = clips.ToList();
            return Write(e, folder) != null;
        }
        catch { return false; }
    }

    private static string Write(Entry e, string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, KeyFor(e.Source) + ".json");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(e, Json), Encoding.UTF8);
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    private static Entry? ReadValid(string path, string folder)
    {
        var file = Path.Combine(folder, KeyFor(path) + ".json");
        if (!File.Exists(file)) return null;
        var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file), Json);
        var fi = new FileInfo(path);
        if (e?.Result == null || e.Version != Version || !fi.Exists || e.Size != fi.Length || e.WriteTimeUtcTicks != fi.LastWriteTimeUtc.Ticks)
            return null;
        return e;
    }

    /// <summary>
    /// The last scan of this exact file (same path, size and modification time), or null. <paramref name="settings"/> is the
    /// current <see cref="Fingerprint"/>; when it differs from the one the scan was made with, <see cref="CachedScan.SettingsChanged"/> is set.
    /// </summary>
    public static CachedScan? TryLoad(string path, string? settings = null, string? folder = null)
    {
        try
        {
            var e = ReadValid(path, folder ?? DefaultFolder);
            if (e == null) return null;
            bool changed = settings != null && e.Settings != null && e.Settings != settings;
            return new CachedScan(e.Result!, e.SavedAt, e.Clips, changed);
        }
        catch { return null; }
    }
}

/// <summary>A cached scan, plus the user's edited clip list if they changed it (null = use the suggested clips).</summary>
public sealed record CachedScan(ScanResult Result, DateTime SavedAt, List<SavedClip>? Clips, bool SettingsChanged);

/// <summary>
/// One clip as the user left it. <see cref="Candidate"/> is its index in <see cref="ScanResult.Candidates"/>,
/// or null for a clip added by hand (then <see cref="ManualCenter"/> is where the playhead was).
/// </summary>
public sealed record SavedClip(double Start, double End, bool Keep, int? Candidate, double ManualCenter);

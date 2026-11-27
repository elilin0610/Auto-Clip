using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ApexHighlighter.Core;

// =====================================================================================
// FFmpegLocator.cs — 找到 ffmpeg.exe / ffprobe.exe 並執行它們
//
// 依序找：環境變數 APEXHL_FFMPEG_DIR → 程式旁邊（或旁邊的 ffmpeg 資料夾）→ PATH → winget 安裝位置。
// 安全：只接受絕對路徑（PATH 裡的相對路徑、"." 一律跳過，避免從目前資料夾載入別人放的 ffmpeg.exe）；
//       winget 只找 Gyan.FFmpeg / BtbN.FFmpeg 這兩個套件的資料夾，不再整個 Packages 遞迴亂找。
//       找到後跑一次 `ffmpeg -version`，記下版本；太舊（< 6.0）或看不出版本就在 log 提醒使用者更新。
// RunAsync：執行一次、等它結束、回傳輸出；失敗會丟出例外並附上錯誤訊息最後一段（Tail）。
// =====================================================================================

/// <summary>Where ffmpeg.exe was found.</summary>
public enum FFmpegSource { EnvVar, AppFolder, Path, WinGet }

/// <summary>
/// Finds ffmpeg.exe / ffprobe.exe. Order: APEXHL_FFMPEG_DIR env var, "ffmpeg" folder next to the app, PATH, winget install.
/// Only absolute folders are accepted, and only the known winget FFmpeg packages are searched.
/// </summary>
public static class FFmpegLocator
{
    /// <summary>Oldest FFmpeg release this app expects. Older builds still run, but the app warns, because FFmpeg parses the recordings and old versions have known security bugs.</summary>
    public static readonly Version MinimumVersion = new(6, 0);

    /// <summary>winget package folders that are known FFmpeg builds (folder names start with these).</summary>
    internal static readonly string[] WinGetPackages = ["Gyan.FFmpeg", "BtbN.FFmpeg"];

    private static (string Dir, FFmpegSource Source)? _found;

    public static string FFmpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string FFprobe => Path.Combine(Dir, "ffprobe.exe");

    public static string Dir => Found.Dir;
    public static FFmpegSource Source => Found.Source;

    private static (string Dir, FFmpegSource Source) Found =>
        _found ??= Find() ?? throw new FileNotFoundException(Loc.T("err.ffmpegMissing"));

    public static bool TryFind(out string? dir)
    {
        try { dir = Dir; return true; } catch { dir = null; return false; }
    }

    private static bool Ok(string? d) =>
        !string.IsNullOrWhiteSpace(d) && Path.IsPathFullyQualified(d)
        && File.Exists(Path.Combine(d, "ffmpeg.exe")) && File.Exists(Path.Combine(d, "ffprobe.exe"));

    private static (string, FFmpegSource)? Find()
    {
        var env = Environment.GetEnvironmentVariable("APEXHL_FFMPEG_DIR")?.Trim();
        if (Ok(env)) return (env!, FFmpegSource.EnvVar);

        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Ok(local)) return (local, FFmpegSource.AppFolder);
        if (Ok(AppContext.BaseDirectory)) return (AppContext.BaseDirectory, FFmpegSource.AppFolder);

        foreach (var p in PathCandidates(Environment.GetEnvironmentVariable("PATH")))
            if (Ok(p)) return (p, FFmpegSource.Path);

        // winget installs — PATH may not be refreshed in already-running sessions.
        var pkgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(pkgs))
        {
            foreach (var pkg in Directory.EnumerateDirectories(pkgs))
            {
                var name = Path.GetFileName(pkg);
                if (!WinGetPackages.Any(w => name.StartsWith(w, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var exe in Directory.EnumerateFiles(pkg, "ffprobe.exe", SearchOption.AllDirectories))
                {
                    var d = Path.GetDirectoryName(exe);
                    if (Ok(d)) return (d!, FFmpegSource.WinGet);
                }
            }
        }
        return null;
    }

    /// <summary>The PATH entries that are safe to search: absolute folders only ("." and relative entries are skipped).</summary>
    internal static IEnumerable<string> PathCandidates(string? path) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0 && Path.IsPathFullyQualified(p));

    // ───────────── version check ─────────────

    /// <summary>What `ffmpeg -version` reported.</summary>
    public sealed record VersionInfo(string Text, Version? Release, DateOnly? BuildDate)
    {
        /// <summary>True when the build is known to be older than <see cref="MinimumVersion"/>.</summary>
        public bool TooOld => Release != null ? Release < MinimumVersion
            : BuildDate != null && BuildDate < new DateOnly(2023, 2, 28);   // FFmpeg 6.0 came out 2023-02-28
        /// <summary>True when the version could not be read (unusual build).</summary>
        public bool Unknown => Release == null && BuildDate == null;
    }

    private static VersionInfo? _version;

    /// <summary>Runs `ffmpeg -version` once and caches the answer. Null when FFmpeg is missing or did not answer.</summary>
    public static async Task<VersionInfo?> GetVersionAsync(CancellationToken ct = default)
    {
        if (_version != null) return _version;
        if (!TryFind(out _)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = await RunAsync(FFmpeg, ["-hide_banner", "-version"], timeout.Token);
            return _version = ParseVersion(output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// Reads the first line of `ffmpeg -version`. Handles "ffmpeg version 7.1-full_build-www.gyan.dev",
    /// "ffmpeg version n7.1-..." (BtbN releases), "ffmpeg version 2025-01-02-git-..." (Gyan git builds),
    /// and "ffmpeg version N-118000-g..." (nightlies, version unknown).
    /// </summary>
    public static VersionInfo ParseVersion(string output)
    {
        var line = (output ?? "").Split('\n').FirstOrDefault(l => l.Contains("version", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "";
        var m = Regex.Match(line, @"version\s+(\S+)", RegexOptions.IgnoreCase);
        var token = m.Success ? m.Groups[1].Value : "";
        var date = Regex.Match(token, @"^(\d{4})-(\d{2})-(\d{2})");
        if (date.Success && DateOnly.TryParseExact(date.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return new VersionInfo(token, null, d);
        var ver = Regex.Match(token, @"^n?(\d+)\.(\d+)(?:\.(\d+))?");
        if (ver.Success)
        {
            int major = int.Parse(ver.Groups[1].Value, CultureInfo.InvariantCulture), minor = int.Parse(ver.Groups[2].Value, CultureInfo.InvariantCulture);
            int patch = ver.Groups[3].Success ? int.Parse(ver.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            return new VersionInfo(token, new Version(major, minor, patch), null);
        }
        return new VersionInfo(token.Length > 0 ? token : "?", null, null);
    }

    /// <summary>One log line: where FFmpeg was found, which version, and a warning when it is too old or unknown.</summary>
    public static async Task<string> DescribeAsync(CancellationToken ct = default)
    {
        if (!TryFind(out var dir)) return Loc.T("err.ffmpegMissing");
        var v = await GetVersionAsync(ct);
        var line = Loc.F("log.ffmpegFound", dir, v?.Text ?? "?", Loc.T("ffmpeg.source." + Source));
        if (v == null || v.Unknown) line += " " + Loc.T("log.ffmpegVersionUnknown");
        else if (v.TooOld) line += " " + Loc.F("log.ffmpegTooOld", MinimumVersion.ToString(2));
        return line;
    }

    /// <summary>Runs a tool to completion and returns stdout. Throws on non-zero exit.</summary>
    public static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;
        if (p.ExitCode != 0)
            throw new InvalidOperationException(Loc.F("err.toolFailed", Path.GetFileName(exe), p.ExitCode, Tail(stderr, 1500)));
        return stdout;
    }

    internal static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}

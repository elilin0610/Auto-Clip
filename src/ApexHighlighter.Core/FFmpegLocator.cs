using System.Diagnostics;

namespace ApexHighlighter.Core;

/// <summary>Finds ffmpeg.exe / ffprobe.exe. Order: APEXHL_FFMPEG_DIR env var, "ffmpeg" folder next to the app, PATH, winget install.</summary>
public static class FFmpegLocator
{
    private static string? _dir;

    public static string FFmpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string FFprobe => Path.Combine(Dir, "ffprobe.exe");

    public static string Dir => _dir ??= Find() ?? throw new FileNotFoundException(
        "找不到 ffmpeg.exe / ffprobe.exe。請安裝 FFmpeg（例如 winget install Gyan.FFmpeg），或把兩個 exe 放到程式資料夾下的 ffmpeg 子資料夾。");

    public static bool TryFind(out string? dir)
    {
        try { dir = Dir; return true; } catch { dir = null; return false; }
    }

    private static string? Find()
    {
        static bool Ok(string? d) => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "ffmpeg.exe")) && File.Exists(Path.Combine(d, "ffprobe.exe"));

        var env = Environment.GetEnvironmentVariable("APEXHL_FFMPEG_DIR");
        if (Ok(env)) return env;

        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Ok(local)) return local;
        if (Ok(AppContext.BaseDirectory)) return AppContext.BaseDirectory;

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (Ok(p.Trim())) return p.Trim();

        // winget installs (Gyan.FFmpeg / BtbN) — PATH may not be refreshed in already-running sessions.
        var pkgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(pkgs))
        {
            foreach (var exe in Directory.EnumerateFiles(pkgs, "ffprobe.exe", SearchOption.AllDirectories))
            {
                var d = Path.GetDirectoryName(exe);
                if (Ok(d)) return d;
            }
        }
        return null;
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
            throw new InvalidOperationException($"{Path.GetFileName(exe)} 失敗 (exit {p.ExitCode}):\n{Tail(stderr, 1500)}");
        return stdout;
    }

    internal static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}

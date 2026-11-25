using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ApexHighlighter.Core;

public sealed class ExportOptions
{
    public int AudioTrack { get; set; } = 0;
    /// <summary>"auto" tries NVENC (GPU) first and falls back to libx264.</summary>
    public string Encoder { get; set; } = "auto";
    public int Quality { get; set; } = 19;
}

public sealed record ExportCheck(bool Ok, double Duration, double VideoDuration, double AudioDuration, double VideoStart, double AudioStart, int Width, int Height, string Summary);

/// <summary>Cuts [start,end] out of the source into a new 16:9 MP4. The source file is only ever read.</summary>
public static class Exporter
{
    private static bool? _nvencOk;

    /// <summary>16:9 canvas that fits the source without scaling (pads with black, keeps every source pixel and the original aspect).</summary>
    public static (int W, int H) Canvas(int srcW, int srcH)
    {
        int w = srcW, h = srcH;
        if (w * 9 > h * 16) h = (int)Math.Ceiling(w * 9 / 16.0);   // wider than 16:9 → pad top/bottom
        else if (w * 9 < h * 16) w = (int)Math.Ceiling(h * 16 / 9.0); // taller (16:10, 4:3) → pad left/right
        return (w + (w % 2), h + (h % 2));
    }

    public static async Task<string> ExportAsync(MediaInfo info, double start, double end, string output, ExportOptions opt,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (end <= start) throw new ArgumentException("結束時間必須晚於開始時間。");
        var src = Path.GetFullPath(info.Path);
        var dst = Path.GetFullPath(output);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("輸出檔不能覆蓋原始錄影。");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        var (cw, ch) = Canvas(info.Width, info.Height);
        var vf = new List<string>();
        if (cw != info.Width || ch != info.Height)
            vf.Add($"pad={cw}:{ch}:(ow-iw)/2:(oh-ih)/2:color=black");
        vf.Add("setsar=1");
        vf.Add("format=yuv420p");

        string encoder = opt.Encoder;
        if (encoder == "auto") encoder = await NvencAvailableAsync(ct) ? "h264_nvenc" : "libx264";

        var tmp = dst + ".part.mp4";
        try
        {
            await RunEncodeAsync(info, start, end, tmp, vf, encoder, opt, progress, ct);
        }
        catch (InvalidOperationException) when (encoder == "h264_nvenc" && !ct.IsCancellationRequested)
        {
            _nvencOk = false;
            await RunEncodeAsync(info, start, end, tmp, vf, "libx264", opt, progress, ct);
        }
        File.Move(tmp, dst, overwrite: true);
        return dst;
    }

    private static async Task RunEncodeAsync(MediaInfo info, double start, double end, string output, List<string> vf, string encoder,
        ExportOptions opt, IProgress<double>? progress, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y",
            // Input seeking + re-encode = frame-accurate cut, and both streams restart at 0 together.
            "-ss", start.ToString("0.###", inv), "-i", info.Path, "-t", (end - start).ToString("0.###", inv),
            "-map", "0:v:0",
        };
        if (info.AudioTracks.Count > 0)
            args.AddRange(["-map", $"0:a:{Math.Clamp(opt.AudioTrack, 0, info.AudioTracks.Count - 1)}"]);
        args.AddRange(["-vf", string.Join(",", vf)]);
        if (encoder == "h264_nvenc")
            args.AddRange(["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", opt.Quality.ToString(inv), "-b:v", "0", "-profile:v", "high"]);
        else
            args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", opt.Quality.ToString(inv), "-profile:v", "high"]);
        if (info.AudioTracks.Count > 0)
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-af", "aresample=async=1:first_pts=0"]);
        args.AddRange(["-fps_mode", "cfr", "-r", (info.Fps > 0 ? Math.Round(info.Fps, 3) : 60).ToString(inv),
            "-movflags", "+faststart", "-avoid_negative_ts", "make_zero", "-map_metadata", "-1", output]);

        var psi = new ProcessStartInfo(FFmpegLocator.FFmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        double total = end - start;
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var us))
                progress?.Report(Math.Clamp(us / 1e6 / total, 0, 1));
        }
        await p.WaitForExitAsync(CancellationToken.None);
        var err = await errTask;
        if (ct.IsCancellationRequested) { TryDelete(output); ct.ThrowIfCancellationRequested(); }
        if (p.ExitCode != 0)
        {
            TryDelete(output);
            throw new InvalidOperationException($"匯出失敗（{encoder}）：{FFmpegLocator.Tail(err, 1200)}");
        }
        progress?.Report(1);
    }

    public static async Task<bool> NvencAvailableAsync(CancellationToken ct)
    {
        if (_nvencOk is { } v) return v;
        try
        {
            await FFmpegLocator.RunAsync(FFmpegLocator.FFmpeg,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.2", "-c:v", "h264_nvenc", "-f", "null", "-"], ct);
            _nvencOk = true;
        }
        catch (OperationCanceledException) { throw; }
        catch { _nvencOk = false; }
        return _nvencOk.Value;
    }

    /// <summary>Checks the exported file: fully decodable, 16:9, and audio/video start and length line up.</summary>
    public static async Task<ExportCheck> VerifyAsync(string file, double expectedDuration, CancellationToken ct)
    {
        var json = await FFmpegLocator.RunAsync(FFmpegLocator.FFprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file], ct);
        using var doc = JsonDocument.Parse(json);
        double fmtDur = Num(doc.RootElement.GetProperty("format"), "duration");
        double vDur = 0, aDur = 0, vStart = 0, aStart = 0; int w = 0, h = 0; bool hasAudio = false;
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            if (type == "video") { vDur = Num(s, "duration"); vStart = Num(s, "start_time"); w = s.GetProperty("width").GetInt32(); h = s.GetProperty("height").GetInt32(); }
            if (type == "audio") { hasAudio = true; aDur = Num(s, "duration"); aStart = Num(s, "start_time"); }
        }

        // Full decode: catches broken files that ffprobe alone would accept.
        string decodeErr = "";
        try
        {
            await FFmpegLocator.RunAsync(FFmpegLocator.FFmpeg, ["-v", "error", "-xerror", "-i", file, "-f", "null", "-"], ct);
        }
        catch (InvalidOperationException ex) { decodeErr = ex.Message; }

        var problems = new List<string>();
        if (decodeErr.Length > 0) problems.Add("完整解碼失敗");
        if (w * 9 != h * 16 && Math.Abs(w / (double)h - 16.0 / 9) > 0.002) problems.Add($"畫面不是 16:9（{w}x{h}）");
        if (!hasAudio) problems.Add("沒有音訊");
        if (hasAudio && Math.Abs(vStart - aStart) > 0.05) problems.Add($"音訊與畫面起點差 {Math.Abs(vStart - aStart) * 1000:0} ms");
        if (hasAudio && Math.Abs(vDur - aDur) > 0.1) problems.Add($"音訊與畫面長度差 {Math.Abs(vDur - aDur) * 1000:0} ms");
        if (Math.Abs(fmtDur - expectedDuration) > 0.5) problems.Add($"長度 {fmtDur:0.00}s 與預期 {expectedDuration:0.00}s 不符");

        var summary = problems.Count == 0
            ? $"OK：{w}x{h}，長度 {fmtDur:0.00}s，影/音起點差 {Math.Abs(vStart - aStart) * 1000:0} ms，長度差 {Math.Abs(vDur - aDur) * 1000:0} ms，完整解碼無錯誤"
            : "問題：" + string.Join("；", problems);
        return new ExportCheck(problems.Count == 0, fmtDur, vDur, aDur, vStart, aStart, w, h, summary);
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    public static string SuggestName(MediaInfo info, int index, double start, double end)
    {
        var baseName = Path.GetFileNameWithoutExtension(info.Path);
        return $"{baseName}_精華{index}_{TimeFmt.Hms(start).Replace(':', '-')}-{TimeFmt.Hms(end).Replace(':', '-')}.mp4";
    }
}

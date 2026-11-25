using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ApexHighlighter.Core;

/// <summary>Landscape keeps the whole frame (16:9, black bars if needed). The vertical ones are 1080×1920 for Shorts.</summary>
public enum ExportFormat { Landscape, VerticalCrop, VerticalFit }

public sealed class ExportOptions
{
    public int AudioTrack { get; set; } = 0;
    public ExportFormat Format { get; set; } = ExportFormat.Landscape;
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

    public const int VerticalW = 1080, VerticalH = 1920;

    /// <summary>Size of the exported video for a given source and format.</summary>
    public static (int W, int H) OutputSize(int srcW, int srcH, ExportFormat format) =>
        format == ExportFormat.Landscape ? Canvas(srcW, srcH) : (VerticalW, VerticalH);

    /// <summary>The -vf filtergraph for a format. Never scales the landscape output; vertical ones are always 1080×1920.</summary>
    public static string VideoFilter(int srcW, int srcH, ExportFormat format)
    {
        const string tail = "setsar=1,format=yuv420p";
        switch (format)
        {
            case ExportFormat.VerticalCrop:
                // The middle 9:16 slice at full height, scaled to 1080×1920.
                return $"crop=trunc(ih*9/16/2)*2:ih:(iw-ow)/2:0,scale={VerticalW}:{VerticalH}:flags=lanczos,{tail}";
            case ExportFormat.VerticalFit:
                // The whole frame fitted to the width, on a blurred, darkened, zoomed copy of itself.
                return $"split=2[bg][fg];" +
                       $"[bg]scale={VerticalW}:{VerticalH}:force_original_aspect_ratio=increase,crop={VerticalW}:{VerticalH},boxblur=24:2,eq=brightness=-0.12[bgb];" +
                       $"[fg]scale={VerticalW}:-2:flags=lanczos[fgs];" +
                       $"[bgb][fgs]overlay=(W-w)/2:(H-h)/2,{tail}";
            default:
                var (cw, ch) = Canvas(srcW, srcH);
                return (cw != srcW || ch != srcH ? $"pad={cw}:{ch}:(ow-iw)/2:(oh-ih)/2:color=black," : "") + tail;
        }
    }

    public static async Task<string> ExportAsync(MediaInfo info, double start, double end, string output, ExportOptions opt,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (end <= start) throw new ArgumentException(Loc.T("err.endBeforeStart"));
        var src = Path.GetFullPath(info.Path);
        var dst = Path.GetFullPath(output);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("err.overwriteSource"));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        var vf = VideoFilter(info.Width, info.Height, opt.Format);

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

    private static async Task RunEncodeAsync(MediaInfo info, double start, double end, string output, string vf, string encoder,
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
        args.AddRange(["-vf", vf]);
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
            throw new InvalidOperationException(Loc.F("err.exportFailed", encoder, FFmpegLocator.Tail(err, 1200)));
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

    /// <summary>Checks the exported file: fully decodable, the right shape (16:9 or 9:16), and audio/video start and length line up.</summary>
    public static async Task<ExportCheck> VerifyAsync(string file, double expectedDuration, CancellationToken ct,
        ExportFormat format = ExportFormat.Landscape)
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
        if (decodeErr.Length > 0) problems.Add(Loc.T("verify.decodeFail"));
        bool vertical = format != ExportFormat.Landscape;
        double want = vertical ? 9.0 / 16 : 16.0 / 9;
        if (h == 0 || Math.Abs(w / (double)h - want) > 0.002) problems.Add(Loc.F("verify.wrongAspect", w, h, vertical ? "9:16" : "16:9"));
        if (!hasAudio) problems.Add(Loc.T("verify.noAudio"));
        if (hasAudio && Math.Abs(vStart - aStart) > 0.05) problems.Add(Loc.F("verify.startDiff", Math.Abs(vStart - aStart) * 1000));
        if (hasAudio && Math.Abs(vDur - aDur) > 0.1) problems.Add(Loc.F("verify.lenDiff", Math.Abs(vDur - aDur) * 1000));
        if (Math.Abs(fmtDur - expectedDuration) > 0.5) problems.Add(Loc.F("verify.durMismatch", fmtDur, expectedDuration));

        var summary = problems.Count == 0
            ? Loc.F("verify.ok", w, h, fmtDur, Math.Abs(vStart - aStart) * 1000, Math.Abs(vDur - aDur) * 1000)
            : Loc.F("verify.problems", string.Join(Loc.T("verify.sep"), problems));
        return new ExportCheck(problems.Count == 0, fmtDur, vDur, aDur, vStart, aStart, w, h, summary);
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    public static string SuggestName(MediaInfo info, int index, double start, double end, ExportFormat format = ExportFormat.Landscape)
    {
        var baseName = Path.GetFileNameWithoutExtension(info.Path);
        var shape = format == ExportFormat.Landscape ? "" : "_9x16";
        return $"{baseName}_{Loc.T("file.highlight")}{index}_{TimeFmt.Hms(start).Replace(':', '-')}-{TimeFmt.Hms(end).Replace(':', '-')}{shape}.mp4";
    }
}

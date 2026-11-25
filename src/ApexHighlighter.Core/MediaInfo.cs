using System.Globalization;
using System.Text.Json;

namespace ApexHighlighter.Core;

public sealed record AudioTrack(int Index, int Channels, string? Language, string? Title, double? MeanVolumeDb = null)
{
    public override string ToString()
    {
        var label = $"音軌 {Index + 1}（{Channels}ch";
        if (!string.IsNullOrWhiteSpace(Title)) label += $"，{Title}";
        if (!string.IsNullOrWhiteSpace(Language) && Language != "und") label += $"，{Language}";
        return label + "）";
    }
}

public sealed record MediaInfo(string Path, double Duration, int Width, int Height, double Fps, string VideoCodec, IReadOnlyList<AudioTrack> AudioTracks)
{
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var json = await FFmpegLocator.RunAsync(FFmpegLocator.FFprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

        int w = 0, h = 0; double fps = 0; string vcodec = "";
        var audio = new List<AudioTrack>();
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            if (type == "video" && w == 0)
            {
                // Skip attached pictures (cover art).
                if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1) continue;
                w = s.GetProperty("width").GetInt32();
                h = s.GetProperty("height").GetInt32();
                vcodec = s.GetProperty("codec_name").GetString() ?? "";
                fps = ParseRate(s.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() : null);
                if (fps <= 0) fps = ParseRate(s.TryGetProperty("r_frame_rate", out var rr) ? rr.GetString() : null);
                if (duration <= 0 && s.TryGetProperty("duration", out var sd))
                    double.TryParse(sd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }
            else if (type == "audio")
            {
                string? lang = null, title = null;
                if (s.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("language", out var l)) lang = l.GetString();
                    if (tags.TryGetProperty("title", out var t)) title = t.GetString();
                    else if (tags.TryGetProperty("handler_name", out var hn) && hn.GetString() is { } hs && !hs.Contains("SoundHandler")) title = hs;
                }
                audio.Add(new AudioTrack(audio.Count, s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0, lang, title));
            }
        }
        if (w == 0) throw new InvalidDataException("檔案裡找不到影片串流。");
        return new MediaInfo(path, duration, w, h, fps, vcodec, audio);
    }

    private static double ParseRate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && b != 0) return a / b;
        return double.TryParse(r, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    /// <summary>Measures mean volume of each audio track over a sample window, so the UI can default to a non-silent track.</summary>
    public async Task<MediaInfo> WithTrackVolumesAsync(CancellationToken ct = default)
    {
        if (AudioTracks.Count <= 1) return this;
        var start = Math.Max(0, Duration / 2 - 30);
        var list = new List<AudioTrack>();
        foreach (var t in AudioTracks)
        {
            double? mean = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(FFmpegLocator.FFmpeg)
                { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-hide_banner", "-nostats", "-ss", start.ToString(CultureInfo.InvariantCulture), "-t", "60", "-i", Path,
                             "-map", $"0:a:{t.Index}", "-af", "volumedetect", "-f", "null", "-" })
                    psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi)!;
                var err = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                var idx = err.LastIndexOf("mean_volume:", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var tail = err[(idx + 12)..].Trim().Split(' ')[0];
                    if (double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) mean = v;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            list.Add(t with { MeanVolumeDb = mean });
        }
        return this with { AudioTracks = list };
    }

    /// <summary>Default export track: first track unless it is near-silent and another track has sound.</summary>
    public int DefaultAudioTrack()
    {
        if (AudioTracks.Count == 0) return -1;
        var first = AudioTracks[0];
        if (first.MeanVolumeDb is null || first.MeanVolumeDb > -60) return 0;
        var loud = AudioTracks.Where(t => t.MeanVolumeDb > -60).OrderByDescending(t => t.MeanVolumeDb).FirstOrDefault();
        return loud?.Index ?? 0;
    }
}

using System.Diagnostics;
using System.Globalization;

namespace ApexHighlighter.Core;

// =====================================================================================
// AudioActivity.cs — 用音量找「交戰從哪裡開始」
//
// 用 FFmpeg 把指定音軌每 0.5 秒算一次音量（dB），得到一條「音量曲線」。
// EstimateFightStart：從第一個擊殺提示往回走，只要聲音一直「很吵」（中間沒有超過 3 秒的安靜）就繼續往前，
//   停下來的地方就是交戰開始；結果限制在提示前 8~25 秒之間。
// 沒有音軌或分析失敗時，Scanner 會改用固定往前推的秒數。
// =====================================================================================

/// <summary>Loudness envelope (dB per window) of one audio track. Used only to decide how far before the first knock a fight started.</summary>
public sealed class AudioActivity
{
    public double Window { get; }
    public float[] Db { get; }
    public double ActiveThresholdDb { get; }

    private AudioActivity(double window, float[] db)
    {
        Window = window;
        Db = db;
        if (db.Length == 0) { ActiveThresholdDb = double.MaxValue; return; }
        var sorted = db.Where(x => x > -90).OrderBy(x => x).ToArray();
        // "Active" = louder than the 65th percentile of the whole match (gunfire, abilities, shouting).
        ActiveThresholdDb = sorted.Length == 0 ? double.MaxValue : sorted[(int)(sorted.Length * 0.65)];
    }

    public static async Task<AudioActivity?> AnalyzeAsync(MediaInfo info, int track, CancellationToken ct, double window = 0.5)
    {
        if (track < 0 || track >= info.AudioTracks.Count) return null;
        const int rate = 8000;
        var psi = new ProcessStartInfo(FFmpegLocator.FFmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-i", info.Path, "-vn", "-sn",
                     "-map", $"0:a:{track}", "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        int perWin = (int)(rate * window);
        var result = new List<float>();
        var buf = new byte[perWin * 2];
        var s = p.StandardOutput.BaseStream;
        while (true)
        {
            int read = 0;
            while (read < buf.Length)
            {
                int n = await s.ReadAsync(buf.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read < 2) break;
            double sum = 0;
            int samples = read / 2;
            for (int i = 0; i < samples; i++)
            {
                double v = BitConverter.ToInt16(buf, i * 2) / 32768.0;
                sum += v * v;
            }
            var rms = Math.Sqrt(sum / samples);
            result.Add((float)(20 * Math.Log10(Math.Max(rms, 1e-5))));
            if (read < buf.Length) break;
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return new AudioActivity(window, result.ToArray());
    }

    public bool IsActive(double t)
    {
        int i = (int)(t / Window);
        return i >= 0 && i < Db.Length && Db[i] >= ActiveThresholdDb;
    }

    /// <summary>
    /// Walks backwards from <paramref name="firstEvent"/> while the audio stays "busy" (no quiet gap longer than <paramref name="maxQuietGap"/>).
    /// Returns the estimated fight start, clamped to [firstEvent - maxBack, firstEvent - minBack].
    /// </summary>
    public double EstimateFightStart(double firstEvent, double minBack, double maxBack, double maxQuietGap = 3.0)
    {
        double lower = Math.Max(0, firstEvent - maxBack);
        double upper = Math.Max(0, firstEvent - minBack);
        double lastActive = firstEvent;
        for (double t = firstEvent; t >= lower; t -= Window)
        {
            if (IsActive(t)) lastActive = t;
            else if (lastActive - t > maxQuietGap) break;
        }
        var start = lastActive - 1.5;
        return Math.Clamp(start, lower, upper);
    }
}

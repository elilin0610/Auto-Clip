using System.Diagnostics;
using System.Globalization;

namespace ApexHighlighter.Core;

public sealed record Frame(int Index, double Time, byte[] Bgra, int Width, int Height);

/// <summary>Streams downscaled BGRA frames out of ffmpeg at a fixed sample rate (no temp files, source untouched).</summary>
public static class FrameReader
{
    public static async IAsyncEnumerable<Frame> ReadAsync(MediaInfo info, double sampleFps, int targetWidth,
        bool useHwDecode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int w = Math.Min(targetWidth, info.Width);
        w -= w % 2;
        int h = (int)Math.Round(info.Height * (double)w / info.Width);
        h -= h % 2;
        int frameBytes = w * h * 4;

        var psi = new ProcessStartInfo(FFmpegLocator.FFmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error" };
        if (useHwDecode) args.AddRange(["-hwaccel", "auto"]);
        args.AddRange(["-i", info.Path, "-an", "-sn", "-dn",
            "-vf", $"fps={sampleFps.ToString(CultureInfo.InvariantCulture)},scale={w}:{h}:flags=area",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var stream = p.StandardOutput.BaseStream;
        int index = 0;
        try
        {
            while (true)
            {
                var buf = new byte[frameBytes];
                int read = 0;
                while (read < frameBytes)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(read, frameBytes - read), ct);
                    if (n == 0) break;
                    read += n;
                }
                if (read < frameBytes) break;
                yield return new Frame(index, index / sampleFps, buf, w, h);
                index++;
            }
        }
        finally
        {
            if (!p.HasExited) { try { p.Kill(true); } catch { } }
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var err = await errTask;
        if (index == 0)
            throw new InvalidOperationException(Loc.F("err.noFrames", FFmpegLocator.Tail(err, 1000)));
    }
}

using System.Diagnostics;
using System.Globalization;

namespace ApexHighlighter.Core;

/// <summary>Small preview pictures for the clip list. Read straight from FFmpeg's output; nothing is written to disk.</summary>
public static class Thumbnail
{
    /// <summary>One frame at <paramref name="time"/> as a JPEG, <paramref name="width"/> pixels wide. Returns null if it can't be read.</summary>
    public static async Task<byte[]?> GrabJpegAsync(string path, double time, int width, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(FFmpegLocator.FFmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
        {
            "-v", "error", "-ss", Math.Max(0, time).ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
            "-frames:v", "1", "-vf", $"scale={width}:-2:flags=area", "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "4", "pipe:1",
        })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode == 0 && ms.Length > 0 ? ms.ToArray() : null;
    }
}

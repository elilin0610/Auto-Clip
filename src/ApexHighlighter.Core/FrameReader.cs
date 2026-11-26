using System.Buffers;
using System.Diagnostics;
using System.Globalization;

namespace ApexHighlighter.Core;

// =====================================================================================
// FrameReader.cs — 從影片一張一張取出畫面
//
// 叫 FFmpeg 用固定 fps（預設每秒 2 張）解碼、縮到 OCR 寬度（預設 1280），
// 輸出原始像素（BGRA，每個像素 4 bytes），用 IAsyncEnumerable 一張一張交給 Scanner。
// 預設讓 FFmpeg 自動挑可用的硬體解碼（-hwaccel auto），比較快；不能用時它會退回 CPU。
// 不寫暫存檔，也不會動到原始錄影。
// 畫面的記憶體從 ArrayPool 借（每張約 3.7 MB），用完由 Scanner 呼叫 Release() 還回去，不用每張都重新配置。
// =====================================================================================

/// <summary>One sampled frame. <see cref="Bgra"/> is rented and may be longer than Width×Height×4; call <see cref="Release"/> when done.</summary>
public sealed record Frame(int Index, double Time, byte[] Bgra, int Width, int Height)
{
    private int _released;
    public int Length => Width * Height * 4;

    /// <summary>Gives the pixel buffer back to the pool. Safe to call more than once; the frame must not be used afterwards.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) ArrayPool<byte>.Shared.Return(Bgra);
    }
}

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
                var buf = ArrayPool<byte>.Shared.Rent(frameBytes);
                int read = 0;
                try
                {
                    while (read < frameBytes)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(read, frameBytes - read), ct);
                        if (n == 0) break;
                        read += n;
                    }
                }
                catch { ArrayPool<byte>.Shared.Return(buf); throw; }
                if (read < frameBytes) { ArrayPool<byte>.Shared.Return(buf); break; }
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

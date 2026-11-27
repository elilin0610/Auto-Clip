using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ApexHighlighter.Core;


// =====================================================================================
// Ocr.cs — 呼叫 Windows 內建的文字辨識（Windows.Media.Ocr）
//
// 不用另外安裝或付費，Windows 有裝哪些語言包就能辨識哪些語言。
// ReadAsync：給一張 BGRA 畫面，回傳每一行文字和它在畫面上的位置（0~1 的比例座標）。
// =====================================================================================

public sealed record OcrTextLine(string Text, double X, double Y, double W, double H);

/// <summary>A part of the frame, as fractions (0–1) of its width and height.</summary>
public readonly record struct FrameRegion(double X, double Y, double W, double H)
{
    /// <summary>The whole box grown by a margin, at least the minimum size (centered), clamped to the frame.</summary>
    public static FrameRegion Around(double x0, double y0, double x1, double y1, double marginX, double marginY, double minW, double minH)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double w = Math.Max(x1 - x0 + 2 * marginX, minW), h = Math.Max(y1 - y0 + 2 * marginY, minH);
        double l = Math.Clamp(cx - w / 2, 0, 1), t = Math.Clamp(cy - h / 2, 0, 1);
        double r = Math.Clamp(cx + w / 2, 0, 1), b = Math.Clamp(cy + h / 2, 0, 1);
        return new FrameRegion(l, t, r - l, b - t);
    }

    public double Area => W * H;
}

/// <summary>Thin wrapper over the built-in Windows OCR (Windows.Media.Ocr). No downloads, no paid API.</summary>
public sealed class OcrReader
{
    private readonly OcrEngine _engine;
    public string LanguageTag { get; }

    private OcrReader(OcrEngine e, string tag) { _engine = e; LanguageTag = tag; }

    public static IReadOnlyList<string> InstalledLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>Picks an installed OCR language matching any of the given tags (prefix match), or null.</summary>
    public static string? ResolveTag(IEnumerable<string> wanted)
    {
        var installed = InstalledLanguages();
        foreach (var w in wanted)
        {
            var hit = installed.FirstOrDefault(i => i.Equals(w, StringComparison.OrdinalIgnoreCase))
                      ?? installed.FirstOrDefault(i => i.StartsWith(w, StringComparison.OrdinalIgnoreCase) || w.StartsWith(i, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    public static OcrReader Create(string tag)
    {
        var e = OcrEngine.TryCreateFromLanguage(new Language(tag))
                ?? throw new InvalidOperationException(Loc.F("err.ocrCreate", tag));
        return new OcrReader(e, tag);
    }

    /// <summary>
    /// Reads all text lines in the frame, or only inside <paramref name="region"/>. Positions are always fractions of the whole frame.
    /// </summary>
    public async Task<List<OcrTextLine>> ReadAsync(byte[] bgra, int width, int height, FrameRegion? region = null)
    {
        int ox = 0, oy = 0, cw = width, ch = height;
        byte[] src = bgra;
        byte[]? rented = null;
        if (region is { } rg)
        {
            ox = Math.Clamp((int)(rg.X * width), 0, width - 1);
            oy = Math.Clamp((int)(rg.Y * height), 0, height - 1);
            cw = Math.Clamp((int)Math.Ceiling(rg.W * width), 1, width - ox);
            ch = Math.Clamp((int)Math.Ceiling(rg.H * height), 1, height - oy);
            if (cw < 40 || ch < 40) { ox = oy = 0; cw = width; ch = height; }   // OCR needs at least 40×40
            else
            {
                rented = ArrayPool<byte>.Shared.Rent(cw * ch * 4);
                for (int y = 0; y < ch; y++)
                    Buffer.BlockCopy(bgra, ((oy + y) * width + ox) * 4, rented, y * cw * 4, cw * 4);
                src = rented;
            }
        }
        OcrResult result;
        try
        {
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(src.AsBuffer(0, cw * ch * 4), BitmapPixelFormat.Bgra8, cw, ch, BitmapAlphaMode.Ignore);
            result = await _engine.RecognizeAsync(bmp);
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
        var lines = new List<OcrTextLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
                x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
            }
            if (line.Words.Count == 0) continue;
            lines.Add(new OcrTextLine(line.Text, (ox + x0) / width, (oy + y0) / height, (x1 - x0) / width, (y1 - y0) / height));
        }
        return lines;
    }
}

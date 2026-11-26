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

    public async Task<List<OcrTextLine>> ReadAsync(byte[] bgra, int width, int height)
    {
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await _engine.RecognizeAsync(bmp);
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
            lines.Add(new OcrTextLine(line.Text, x0 / width, y0 / height, (x1 - x0) / width, (y1 - y0) / height));
        }
        return lines;
    }
}

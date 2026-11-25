using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexHighlighter.Core;

/// <summary>Writes the scan log (JSON + readable text) so misdetections can be reviewed later.</summary>
public static class ScanReport
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexHighlighter", "scans");

    public static (string Json, string Text) Save(ScanResult r, string? folder = null)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(r.File)}_{DateTime.Now:yyyyMMdd-HHmmss}");
        var json = stem + ".json";
        var txt = stem + ".txt";
        File.WriteAllText(json, JsonSerializer.Serialize(r, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        }), Encoding.UTF8);
        File.WriteAllText(txt, ToText(r), Encoding.UTF8);
        return (json, txt);
    }

    public static string ToText(ScanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"檔案：{r.File}");
        sb.AppendLine($"長度：{TimeFmt.Hms(r.Duration)}（{r.Resolution}）");
        sb.AppendLine($"掃描耗時：{r.ScanSeconds:0.0} 秒（{r.FramesScanned} 張取樣，約 {(r.ScanSeconds > 0 ? r.Duration / r.ScanSeconds : 0):0.0} 倍速）");
        sb.AppendLine($"遊戲語言：{r.Language}（OCR {r.OcrLanguage}{(r.LanguageVerified ? "" : "，關鍵字未經實測")}）");
        if (r.PromptPosition != null) sb.AppendLine(r.PromptPosition);
        if (r.SpectatingFramesSkipped > 0) sb.AppendLine($"觀戰畫面略過：{r.SpectatingFramesSkipped} 張");
        foreach (var n in r.Notes) sb.AppendLine("備註：" + n);
        sb.AppendLine();
        sb.AppendLine($"偵測到的提示（去重後 {r.Events.Count} 個）：");
        foreach (var e in r.Events)
            sb.AppendLine($"  {TimeFmt.Hms(e.Time)}  {e.Kind.Zh(),-6} 出現 {e.Samples} 次  「{e.Text}」");
        sb.AppendLine();
        sb.AppendLine($"交戰（{r.Clusters.Count} 波，建議 {r.Candidates.Count} 段）：");
        int i = 0;
        foreach (var c in r.Clusters)
        {
            sb.AppendLine($"  [{(c.Accepted ? "建議 " + (++i) : "略過")}] {TimeFmt.Hms(c.Start)}–{TimeFmt.Hms(c.End)}  分數 {c.Score:0.0}  {c.Reason}；{c.StartNote}");
            if (!c.Accepted) sb.AppendLine($"         原因：{c.RejectReason}");
        }
        return sb.ToString();
    }
}

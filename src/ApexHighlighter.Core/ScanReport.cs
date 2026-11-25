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
        sb.AppendLine(Loc.F("rep.file", r.File));
        sb.AppendLine(Loc.F("rep.length", TimeFmt.Hms(r.Duration), r.Resolution));
        sb.AppendLine(Loc.F("rep.time", r.ScanSeconds, r.FramesScanned, r.ScanSeconds > 0 ? r.Duration / r.ScanSeconds : 0));
        sb.AppendLine(Loc.F("rep.lang", r.Language, r.OcrLanguage, r.LanguageVerified ? "" : Loc.T("rep.unverified")));
        if (r.PromptPosition != null) sb.AppendLine(r.PromptPosition);
        if (r.SpectatingFramesSkipped > 0) sb.AppendLine(Loc.F("rep.spect", r.SpectatingFramesSkipped));
        foreach (var n in r.Notes) sb.AppendLine(Loc.F("rep.note", n));
        sb.AppendLine();
        sb.AppendLine(Loc.F("rep.events", r.Events.Count));
        foreach (var e in r.Events)
            sb.AppendLine(Loc.F("rep.event", TimeFmt.Hms(e.Time), e.Kind.Name(), e.Samples, e.Text));
        sb.AppendLine();
        sb.AppendLine(Loc.F("rep.clusters", r.Clusters.Count, r.Candidates.Count));
        int i = 0;
        foreach (var c in r.Clusters)
        {
            var tag = c.Accepted ? Loc.F("rep.suggest", ++i) : Loc.T("rep.skip");
            sb.AppendLine(Loc.F("rep.cluster", tag, TimeFmt.Hms(c.Start), TimeFmt.Hms(c.End), c.Score, c.Reason, c.StartNote));
            if (!c.Accepted) sb.AppendLine(Loc.F("rep.why", c.RejectReason));
        }
        return sb.ToString();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using ApexHighlighter.Core;

namespace ApexHighlighter.Tests;

/// <summary>Shared fixtures: the real keyword rules and the OCR hits from an accepted scan (acceptance/ recording B).</summary>
internal static class TestData
{
    private static readonly Lazy<KeywordConfig> _cfg = new(() => KeywordConfig.Load());
    public static KeywordConfig Config => _cfg.Value;

    public static KeywordMatcher English()
    {
        var lang = Config.Languages.First(l => l.Id == "en");
        return new KeywordMatcher(lang, Config.Spectate.TryGetValue("en", out var s) ? s : []);
    }

    private sealed class Report
    {
        public double Duration { get; set; }
        public List<PromptHit> Hits { get; set; } = [];
    }

    /// <summary>Recording B: a 61 s SteelSeries clip with 2 knocks, 3 eliminations, a squad wipe and kill leader (checked by hand).</summary>
    public static (double Duration, List<PromptHit> Hits) ScanB()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "scan-b-hits.json");
        var r = JsonSerializer.Deserialize<Report>(File.ReadAllText(path), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
        return (r.Duration, r.Hits);
    }

    public static GameEvent Ev(double t, EventKind k, int samples = 2, double? last = null) => new(t, last ?? t + 1, k, k.ToString(), samples);

    public static ScanOptions Opt(Action<ScanOptions>? f = null)
    {
        var o = new ScanOptions();
        f?.Invoke(o);
        return o;
    }
}

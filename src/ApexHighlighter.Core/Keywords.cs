using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexHighlighter.Core;

public enum EventKind { Knock, Elimination, SquadWipe, KillLeader, Champion, Assist }

public static class EventKindText
{
    public static string Zh(this EventKind k) => k switch
    {
        EventKind.Knock => "擊倒",
        EventKind.Elimination => "淘汰",
        EventKind.SquadWipe => "全隊淘汰",
        EventKind.KillLeader => "成為擊殺王",
        EventKind.Champion => "冠軍",
        EventKind.Assist => "助攻",
        _ => k.ToString(),
    };
}

public sealed class KeywordRule
{
    [JsonPropertyName("kind")] public EventKind Kind { get; set; }
    [JsonPropertyName("phrases")] public List<string> Phrases { get; set; } = [];
    [JsonPropertyName("forbidWords")] public List<string> ForbidWords { get; set; } = [];
    [JsonPropertyName("forbidPhrases")] public List<string> ForbidPhrases { get; set; } = [];
    [JsonPropertyName("requireAny")] public List<string> RequireAny { get; set; } = [];
    /// <summary>Max characters allowed before the keyword on the same line (icons read as 1–2 junk chars). -1 = any.</summary>
    [JsonPropertyName("maxPrefix")] public int MaxPrefix { get; set; } = -1;
}

public sealed class LanguageRules
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ocr")] public List<string> OcrTags { get; set; } = [];
    [JsonPropertyName("verified")] public bool Verified { get; set; }
    [JsonPropertyName("rules")] public List<KeywordRule> Rules { get; set; } = [];
    /// <summary>Game prompts in this language are rendered in capitals; mixed-case lines (subtitles, item names) are ignored.</summary>
    [JsonPropertyName("upperCaseOnly")] public bool UpperCaseOnly { get; set; }
    /// <summary>Lines containing any of these are consumed without producing an event (e.g. your own squad eliminated).</summary>
    [JsonPropertyName("ignore")] public List<string> Ignore { get; set; } = [];
    /// <summary>HUD text that is on screen all match long; only used to tell which language the game is in.</summary>
    [JsonPropertyName("anchors")] public List<string> Anchors { get; set; } = [];
}

public sealed class KeywordConfig
{
    [JsonPropertyName("weights")] public Dictionary<EventKind, double> Weights { get; set; } = new();
    [JsonPropertyName("spectate")] public Dictionary<string, List<string>> Spectate { get; set; } = new();
    [JsonPropertyName("languages")] public List<LanguageRules> Languages { get; set; } = [];

    private static readonly JsonSerializerOptions Opts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static KeywordConfig Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "keywords.json");
        var cfg = JsonSerializer.Deserialize<KeywordConfig>(File.ReadAllText(path), Opts)
                  ?? throw new InvalidDataException("keywords.json 格式錯誤");
        return cfg;
    }

    public double Weight(EventKind k) => Weights.TryGetValue(k, out var w) ? w : 1.0;
}

/// <summary>Matches OCR lines against one language's rules. Tolerant to OCR noise for Latin text.</summary>
public sealed class KeywordMatcher
{
    private readonly LanguageRules _lang;
    private readonly List<string> _spectate;

    public KeywordMatcher(LanguageRules lang, IEnumerable<string> spectate)
    {
        _lang = lang;
        _spectate = spectate.Select(Norm).ToList();
    }

    public string LanguageId => _lang.Id;

    public bool IsAnchor(string line)
    {
        var n = Norm(line);
        return _lang.Anchors.Any(a => FuzzyContains(n, Norm(a), out _, out _));
    }

    public bool IsSpectating(string line)
    {
        var n = Norm(line);
        return _spectate.Any(s => FuzzyContains(n, s, out _, out _));
    }

    /// <summary>Returns the matched kind and the text after the keyword (usually the victim name), or null.</summary>
    public (EventKind Kind, string After)? Match(string line)
    {
        var n = Norm(line);
        if (n.Length == 0) return null;
        if (_lang.UpperCaseOnly && !MostlyUpper(line)) return null;
        if (_lang.Ignore.Any(ig => FuzzyContains(n, Norm(ig), out _, out _))) return null;
        var words = Words(line);
        foreach (var r in _lang.Rules)
        {
            foreach (var ph in r.Phrases)
            {
                var np = Norm(ph);
                if (!FuzzyContains(n, np, out var startIdx, out var endIdx)) continue;
                if (r.MaxPrefix >= 0 && startIdx > r.MaxPrefix) continue;
                if (r.ForbidWords.Any(fw => words.Contains(fw.ToUpperInvariant()))) goto nextRule;
                if (r.ForbidPhrases.Any(fp => n.Contains(Norm(fp), StringComparison.Ordinal))) goto nextRule;
                if (r.RequireAny.Count > 0 && !r.RequireAny.Any(q => FuzzyContains(n, Norm(q), out _, out _))) goto nextRule;
                var rest = endIdx < n.Length ? n[endIdx..] : "";
                return (r.Kind, rest);
            }
            nextRule:;
        }
        return null;
    }

    /// <summary>At least 80% of the Latin letters are capitals (needs 4+ letters to judge).</summary>
    public static bool MostlyUpper(string s)
    {
        int up = 0, low = 0;
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z') up++;
            else if (c is >= 'a' and <= 'z') low++;
        }
        return up + low < 4 || up >= 0.8 * (up + low);
    }

    /// <summary>Upper-case, strip whitespace and punctuation, full-width → half-width.</summary>
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch0 in s.Normalize(NormalizationForm.FormKC))
        {
            var ch = char.ToUpperInvariant(ch0);
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || cat is UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation
                    or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.MathSymbol
                    or UnicodeCategory.OtherSymbol or UnicodeCategory.ConnectorPunctuation)
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static HashSet<string> Words(string s) =>
        s.ToUpperInvariant().Split([' ', '\t', ',', '.', ':', '!', '-'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>
    /// Substring search allowing small edit distance for ASCII keywords (OCR confuses O/0, I/1, etc).
    /// CJK keywords are short and must match exactly.
    /// </summary>
    public static bool FuzzyContains(string text, string kw, out int startIndex, out int endIndex)
    {
        startIndex = endIndex = -1;
        if (kw.Length == 0) return false;
        var idx = text.IndexOf(kw, StringComparison.Ordinal);
        if (idx >= 0) { startIndex = idx; endIndex = idx + kw.Length; return true; }
        bool ascii = kw.All(c => c < 128);
        if (!ascii || kw.Length < 6) return false;
        int maxDist = kw.Length >= 10 ? 2 : 1;
        int best = int.MaxValue, bestEnd = -1, bestStart = -1;
        for (int len = kw.Length - maxDist; len <= kw.Length + maxDist; len++)
        {
            if (len <= 0 || len > text.Length) continue;
            for (int i = 0; i + len <= text.Length; i++)
            {
                var d = Levenshtein(text.AsSpan(i, len), kw, maxDist);
                if (d < best) { best = d; bestEnd = i + len; bestStart = i; }
            }
        }
        if (best <= maxDist) { startIndex = bestStart; endIndex = bestEnd; return true; }
        return false;
    }

    private static int Levenshtein(ReadOnlySpan<char> a, string b, int cap)
    {
        Span<int> prev = stackalloc int[b.Length + 1];
        Span<int> cur = stackalloc int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = Confusable(a[i - 1], b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > cap) return cap + 1;
            cur.CopyTo(prev);
        }
        return prev[b.Length];
    }

    private static bool Confusable(char x, char y)
    {
        if (x == y) return true;
        static char Canon(char c) => c switch { '0' => 'O', '1' => 'I', 'L' => 'I', '|' => 'I', '5' => 'S', '8' => 'B', _ => c };
        return Canon(x) == Canon(y);
    }

    /// <summary>Similarity 0..1 between two normalized strings (used to tell two different victim names apart).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = Levenshtein(a.AsSpan(), b, Math.Max(a.Length, b.Length));
        return 1.0 - (double)d / Math.Max(a.Length, b.Length);
    }
}

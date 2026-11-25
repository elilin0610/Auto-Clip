using System.Diagnostics;
using System.Threading.Channels;

namespace ApexHighlighter.Core;

/// <summary>
/// Scans a recording for Apex on-screen prompts (knock / elimination / squad wipe / kill leader / champion)
/// using Windows OCR on the whole frame, so neither HUD position nor game language is assumed.
/// </summary>
public sealed class Scanner
{
    private readonly KeywordConfig _cfg;

    public Scanner(KeywordConfig cfg) => _cfg = cfg;

    private sealed record Pair(string Key, string OcrTag, KeywordMatcher Matcher, bool Native);
    private sealed record FrameHits(double Time, string PairKey, List<PromptHit> Hits, bool Spectating, int Anchors);

    public async Task<ScanResult> ScanAsync(MediaInfo info, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult { File = info.Path, Duration = info.Duration, Resolution = $"{info.Width}x{info.Height}" };

        var pairs = BuildPairs(opt.Language, result.Notes);
        if (pairs.Count == 0)
            throw new InvalidOperationException(Loc.T("err.noOcr"));

        var audioTask = AudioActivity.AnalyzeAsync(info, opt.AudioTrack, ct);

        // Language lock: before lock every frame is OCR'd with every (engine, rules) pair; afterwards only the winner.
        Pair? locked = pairs.Count == 1 ? pairs[0] : null;
        var hitCount = pairs.ToDictionary(p => p.Key, _ => 0);
        var gate = new object();

        var channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(opt.Workers * 2) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var collected = new List<FrameHits>();
        int framesDone = 0;
        int totalFrames = Math.Max(1, (int)(info.Duration * opt.SampleFps));

        var engineTags = pairs.Select(p => p.OcrTag).Distinct().ToList();

        var workers = Enumerable.Range(0, opt.Workers).Select(_ => Task.Run(async () =>
        {
            var readers = engineTags.ToDictionary(t => t, OcrReader.Create);
            await foreach (var f in channel.Reader.ReadAllAsync(ct))
            {
                Pair? lk;
                lock (gate) lk = locked;
                var active = lk != null ? [lk] : pairs;
                var linesByTag = new Dictionary<string, List<OcrTextLine>>();
                foreach (var tag in active.Select(a => a.OcrTag).Distinct())
                    linesByTag[tag] = await readers[tag].ReadAsync(f.Bgra, f.Width, f.Height);

                var local = new List<FrameHits>();
                foreach (var pair in active)
                {
                    var lines = linesByTag[pair.OcrTag];
                    bool spect = lines.Any(l => pair.Matcher.IsSpectating(l.Text));
                    int anchors = lk == null ? lines.Count(l => pair.Matcher.IsAnchor(l.Text)) : 0;
                    var hits = new List<PromptHit>();
                    foreach (var l in lines)
                    {
                        var m = pair.Matcher.Match(l.Text);
                        if (m is { } mm) hits.Add(new PromptHit(f.Time, mm.Kind, l.Text, mm.After, l.X, l.Y, l.W, l.H));
                    }
                    local.Add(new FrameHits(f.Time, pair.Key, hits, spect, anchors));
                }

                lock (gate)
                {
                    collected.AddRange(local);
                    if (locked == null)
                    {
                        // Prompts and always-on HUD text (e.g. "SQUADS LEFT") both count as evidence for the language.
                        foreach (var fh in local) if (!fh.Spectating) hitCount[fh.PairKey] += fh.Hits.Count + fh.Anchors;
                        var best = pairs.OrderByDescending(p => hitCount[p.Key]).ThenByDescending(p => p.Native).First();
                        var second = pairs.Where(p => p != best).Select(p => hitCount[p.Key]).DefaultIfEmpty(0).Max();
                        if (hitCount[best.Key] >= 6 && hitCount[best.Key] >= second * 2) locked = best;
                    }
                    framesDone++;
                }
                if (framesDone % 10 == 0)
                {
                    progress?.Report(new ScanProgress
                    {
                        Fraction = Math.Min(1, framesDone / (double)totalFrames),
                        Message = Loc.F("progress.scanning", TimeFmt.Hms(f.Time), TimeFmt.Hms(info.Duration))
                                  + (locked != null ? Loc.F("progress.lang", locked.Matcher.LanguageId) : Loc.T("progress.detecting")),
                    });
                }
            }
        }, ct)).ToArray();

        try
        {
            await foreach (var frame in FrameReader.ReadAsync(info, opt.SampleFps, opt.OcrWidth, opt.HardwareDecode, ct))
                await channel.Writer.WriteAsync(frame, ct);
        }
        finally
        {
            channel.Writer.TryComplete();
        }
        await Task.WhenAll(workers);
        result.FramesScanned = framesDone;

        // Final language choice (may still be null if the match had almost no prompts).
        var winner = locked ?? pairs.OrderByDescending(p => hitCount[p.Key]).ThenByDescending(p => p.Native).First();
        result.Language = winner.Matcher.LanguageId;
        result.OcrLanguage = winner.OcrTag;
        result.LanguageConfident = locked != null || pairs.Count == 1;
        result.LanguageVerified = _cfg.Languages.First(l => l.Id == winner.Matcher.LanguageId).Verified;
        if (locked == null && pairs.Count > 1)
            result.Notes.Add(Loc.F("note.langUncertain", winner.Matcher.LanguageId, hitCount[winner.Key]));
        if (!result.LanguageVerified)
            result.Notes.Add(Loc.F("note.langUnverified", winner.Matcher.LanguageId));

        var mine = collected.Where(c => c.PairKey == winner.Key).OrderBy(c => c.Time).ToList();
        result.SpectatingFramesSkipped = mine.Count(m => m.Spectating);
        result.Hits = mine.Where(m => !m.Spectating).SelectMany(m => m.Hits).OrderBy(h => h.Time).ToList();
        if (MedianPosition(result.Hits) is { } pos) { result.PromptX = pos.X; result.PromptY = pos.Y; }

        result.Events = Deduplicate(result.Hits, opt.SampleFps);

        progress?.Report(new ScanProgress { Fraction = 1, Message = Loc.T("progress.merging") });
        AudioActivity? audio = null;
        try { audio = await audioTask; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Notes.Add(Loc.F("note.audioFailed", ex.Message)); }

        result.Clusters = Cluster(result.Events, audio, info.Duration, opt);
        result.Candidates = result.Clusters.Where(c => c.Accepted).ToList();
        result.ScanSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private List<Pair> BuildPairs(string language, List<string> notes)
    {
        var installed = OcrReader.InstalledLanguages();
        var langs = language == "auto" ? _cfg.Languages : _cfg.Languages.Where(l => l.Id == language).ToList();
        if (langs.Count == 0) throw new ArgumentException(Loc.F("err.langMissing", language));

        var pairs = new List<Pair>();
        foreach (var l in langs)
        {
            var spect = _cfg.Spectate.TryGetValue(l.Id, out var s) ? s : [];
            var matcher = new KeywordMatcher(l, spect);
            var tag = OcrReader.ResolveTag(l.OcrTags);
            if (tag != null)
            {
                pairs.Add(new Pair($"{l.Id}@{tag}", tag, matcher, true));
            }
            else if (l.Rules.Any(r => r.Phrases.Any(p => p.All(c => c < 128))) || language != "auto")
            {
                // Latin-script rules can still be read by any installed engine.
                var fallback = installed.FirstOrDefault(i => i.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? installed.FirstOrDefault();
                if (fallback != null) pairs.Add(new Pair($"{l.Id}@{fallback}", fallback, matcher, false));
                if (language != "auto") notes.Add(Loc.F("note.ocrFallback", l.Id, fallback));
            }
        }
        if (language == "auto")
            notes.Add(Loc.F("note.ocrAvailable", string.Join(Loc.T("list.sep"), installed)));
        return pairs;
    }

    /// <summary>Same kind + similar trailing text (victim name) within a few seconds = the same on-screen prompt.</summary>
    internal static List<GameEvent> Deduplicate(List<PromptHit> hits, double sampleFps)
    {
        var events = new List<(double First, double Last, EventKind Kind, string Name, string Text, int N)>();
        double linger = 4.0;
        foreach (var h in hits.OrderBy(h => h.Time))
        {
            // Victim name = text after the keyword minus the "+150" points and icons.
            var name = new string(h.Rest.Where(char.IsLetter).ToArray());
            int match = -1;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (h.Time - e.Last > linger) break;
                if (e.Kind != h.Kind) continue;
                bool bothNamed = e.Name.Length >= 3 && name.Length >= 3;
                bool sameName = !bothNamed || KeywordMatcher.Similarity(e.Name, name) >= 0.55;
                // Knocks / eliminations can stack (two lines at once) only when the names clearly differ.
                bool stackable = h.Kind is EventKind.Knock or EventKind.Elimination;
                if (h.Time == e.Last && stackable && bothNamed && !sameName) continue;
                if (h.Time == e.Last || sameName) { match = i; break; }
            }
            if (match >= 0)
            {
                var e = events[match];
                bool newSample = h.Time > e.Last;
                events[match] = (e.First, h.Time, e.Kind, e.Name.Length >= name.Length ? e.Name : name, e.Text, e.N + (newSample ? 1 : 0));
            }
            else events.Add((h.Time, h.Time, h.Kind, name, h.Text, 1));
        }
        // A real prompt stays ~2 s, so seeing it in only one sample at >=2 fps is weak; keep it but it scores less in clustering.
        return events.Select(e => new GameEvent(e.First, e.Last, e.Kind, e.Text.Trim(), e.N)).ToList();
    }

    internal List<Candidate> Cluster(List<GameEvent> events, AudioActivity? audio, double duration, ScanOptions opt)
    {
        var groups = new List<List<GameEvent>>();
        foreach (var e in events.OrderBy(e => e.Time))
        {
            if (groups.Count > 0 && e.Time - groups[^1][^1].LastSeen <= opt.MergeGap) groups[^1].Add(e);
            else groups.Add([e]);
        }

        var all = new List<Candidate>();
        foreach (var g in groups)
        {
            double score = 0;
            foreach (var e in g)
                score += _cfg.Weight(e.Kind) * (e.Samples >= 2 ? 1.0 : 0.5);

            double first = g[0].Time, last = g.Max(e => e.LastSeen);
            double start = audio != null
                ? audio.EstimateFightStart(first, opt.MinPreRoll, opt.MaxPreRoll)
                : Math.Max(0, first - (opt.MinPreRoll + opt.MaxPreRoll) / 2);
            double post = opt.PostRoll + (g.Any(e => e.Kind == EventKind.Champion) ? 8 : 0);
            double end = Math.Min(duration, last + post);

            bool champion = g.Any(e => e.Kind == EventKind.Champion && e.Samples >= 2);
            var reject = RejectKind.None;
            if (score < opt.MinScore && !champion) reject = RejectKind.LowScore;
            else if (g.All(e => e.Kind == EventKind.Assist)) reject = RejectKind.AssistOnly;

            all.Add(new Candidate
            {
                Start = start, End = end, FirstEvent = first, LastEvent = last, Score = score, Events = g,
                LeadIn = first - start, LeadInFromAudio = audio != null, Reject = reject, Threshold = opt.MinScore, TopN = opt.MaxCandidates,
            });
        }

        // Keep only the top N; the rest are marked so the report still explains why they were dropped.
        var keep = all.Where(c => c.Accepted).OrderByDescending(c => c.Score).ThenBy(c => c.Start).Take(opt.MaxCandidates).ToHashSet();
        return all.Select(c => c.Accepted && !keep.Contains(c)
            ? new Candidate
            {
                Start = c.Start, End = c.End, FirstEvent = c.FirstEvent, LastEvent = c.LastEvent, Score = c.Score, Events = c.Events,
                LeadIn = c.LeadIn, LeadInFromAudio = c.LeadInFromAudio, Reject = RejectKind.NotTopN, Threshold = c.Threshold, TopN = c.TopN,
            }
            : c).OrderBy(c => c.Start).ToList();
    }

    private static (double X, double Y)? MedianPosition(List<PromptHit> hits)
    {
        var center = hits.Where(h => h.Kind is EventKind.Knock or EventKind.Elimination).ToList();
        if (center.Count == 0) return null;
        var xs = center.Select(h => h.X + h.W / 2).OrderBy(x => x).ToList();
        var ys = center.Select(h => h.Y + h.H / 2).OrderBy(y => y).ToList();
        return (xs[xs.Count / 2], ys[ys.Count / 2]);
    }
}
public static class TimeFmt
{
    public static string Hms(double s)
    {
        if (s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string HmsMs(double s)
    {
        if (s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    public static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        text = text.Trim();
        if (text.Length == 0) return false;
        var parts = text.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return parts.Length <= 3;
    }
}

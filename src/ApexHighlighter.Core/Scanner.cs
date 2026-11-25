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
            throw new InvalidOperationException("找不到可用的 OCR 語言。請在 Windows 設定 > 語言 安裝英文或中文語言包（含 OCR）。");

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
                        Message = $"掃描中 {TimeFmt.Hms(f.Time)} / {TimeFmt.Hms(info.Duration)}" + (locked != null ? $"（語言：{locked.Matcher.LanguageId}）" : "（判斷遊戲語言中）"),
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
        result.LanguageVerified = _cfg.Languages.First(l => l.Id == winner.Matcher.LanguageId).Verified;
        if (locked == null && pairs.Count > 1)
            result.Notes.Add($"畫面上讀不到足夠的提示或 HUD 文字，無法確定遊戲語言；暫用 {winner.Matcher.LanguageId}（命中 {hitCount[winner.Key]} 次）。");
        if (!result.LanguageVerified)
            result.Notes.Add($"「{winner.Matcher.LanguageId}」的關鍵字還沒用實際錄影確認過，結果請多看一眼，必要時修改 keywords.json。");

        var mine = collected.Where(c => c.PairKey == winner.Key).OrderBy(c => c.Time).ToList();
        result.SpectatingFramesSkipped = mine.Count(m => m.Spectating);
        result.Hits = mine.Where(m => !m.Spectating).SelectMany(m => m.Hits).OrderBy(h => h.Time).ToList();
        result.PromptPosition = DescribePosition(result.Hits);

        result.Events = Deduplicate(result.Hits, opt.SampleFps);

        progress?.Report(new ScanProgress { Fraction = 1, Message = "分析音量、合併交戰…" });
        AudioActivity? audio = null;
        try { audio = await audioTask; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Notes.Add("音量分析失敗，片頭改用固定前置秒數：" + ex.Message); }

        result.Clusters = Cluster(result.Events, audio, info.Duration, opt);
        result.Candidates = result.Clusters.Where(c => c.Accepted).ToList();
        result.ScanSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private List<Pair> BuildPairs(string language, List<string> notes)
    {
        var installed = OcrReader.InstalledLanguages();
        var langs = language == "auto" ? _cfg.Languages : _cfg.Languages.Where(l => l.Id == language).ToList();
        if (langs.Count == 0) throw new ArgumentException($"keywords.json 裡沒有語言 {language}");

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
                if (language != "auto") notes.Add($"系統沒有安裝 {l.Id} 的 OCR 語言，改用 {fallback} 引擎讀取。");
            }
        }
        if (language == "auto")
            notes.Add("可用的 OCR 語言：" + string.Join("、", installed) + "；自動判斷遊戲語言。");
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
            double start;
            string startNote;
            if (audio != null)
            {
                start = audio.EstimateFightStart(first, opt.MinPreRoll, opt.MaxPreRoll);
                startNote = $"依音量往前抓 {first - start:0} 秒";
            }
            else
            {
                start = Math.Max(0, first - (opt.MinPreRoll + opt.MaxPreRoll) / 2);
                startNote = $"固定往前 {first - start:0} 秒";
            }
            double post = opt.PostRoll + (g.Any(e => e.Kind == EventKind.Champion) ? 8 : 0);
            double end = Math.Min(duration, last + post);

            var reason = string.Join("、", g.GroupBy(e => e.Kind).OrderBy(k => k.Key)
                .Select(k => k.Count() > 1 ? $"{k.Key.Zh()} ×{k.Count()}" : k.Key.Zh()));
            bool champion = g.Any(e => e.Kind == EventKind.Champion && e.Samples >= 2);
            string? reject = null;
            if (score < opt.MinScore && !champion)
                reject = $"分數 {score:0.0} 低於門檻 {opt.MinScore:0.0}（只有{reason}，不夠確定是精華）";
            else if (g.All(e => e.Kind == EventKind.Assist))
                reject = $"只有助攻（{reason}），主攻是隊友";

            all.Add(new Candidate
            {
                Start = start, End = end, FirstEvent = first, LastEvent = last, Score = score,
                Events = g, Reason = reason, StartNote = startNote, Accepted = reject == null, RejectReason = reject,
            });
        }

        // Keep only the top N; the rest are marked so the report still explains why they were dropped.
        var keep = all.Where(c => c.Accepted).OrderByDescending(c => c.Score).ThenBy(c => c.Start).Take(opt.MaxCandidates).ToHashSet();
        return all.Select(c => c.Accepted && !keep.Contains(c)
            ? new Candidate
            {
                Start = c.Start, End = c.End, FirstEvent = c.FirstEvent, LastEvent = c.LastEvent, Score = c.Score, Events = c.Events,
                Reason = c.Reason, StartNote = c.StartNote, Accepted = false, RejectReason = $"分數 {c.Score:0.0}，排在前 {opt.MaxCandidates} 名之外",
            }
            : c).OrderBy(c => c.Start).ToList();
    }

    private static string? DescribePosition(List<PromptHit> hits)
    {
        var center = hits.Where(h => h.Kind is EventKind.Knock or EventKind.Elimination).ToList();
        if (center.Count == 0) return null;
        var xs = center.Select(h => h.X + h.W / 2).OrderBy(x => x).ToList();
        var ys = center.Select(h => h.Y + h.H / 2).OrderBy(y => y).ToList();
        return $"擊倒／淘汰提示大多在畫面水平 {xs[xs.Count / 2]:P0}、垂直 {ys[ys.Count / 2]:P0} 的位置（從錄影自動量到，不是預設值）";
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

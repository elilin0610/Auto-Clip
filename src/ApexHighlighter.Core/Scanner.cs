using System.Diagnostics;
using System.Threading.Channels;

namespace ApexHighlighter.Core;

// =====================================================================================
// Scanner.cs — 掃描主流程（整個專案最核心的檔案）
//
// 做的事：把一整場錄影「看一遍」，找出畫面上的擊殺提示，再組成最多 3 段精華。
//
// 流程（ScanAsync）：
//   ① FrameReader 用 FFmpeg 每秒取 2 張畫面
//   ② 多個 worker 同時把畫面丟給 Windows OCR，讀出畫面上所有文字
//   ③ KeywordMatcher 比對文字：是不是「擊倒」「淘汰」「全隊淘汰」…
//   ④ 一邊掃一邊判斷遊戲語言（哪個語言的關鍵字命中最多），確定後只用那個語言，速度變快
//      上次判斷出的語言只要少量證據就勝出；掃很久還判斷不出來，就直接用目前最像的
//      語言確定、也量到提示大概在哪之後，大部分畫面只 OCR 那一塊（每隔幾張仍讀整張）
//   ⑤ 去重複：同一個提示會在畫面停 2~3 秒，被讀到好幾次 → 合併成一個事件（Deduplicate）
//   ⑥ 分群：相隔 25 秒內的事件算同一場交戰（Cluster），算分數，用音量找交戰開始時間
//   ⑦ 分數夠高的前 3 名 → 候選片段
//   ⑧ 二次確認：讀得不夠確定的提示用更高 fps 重讀，沒再看到就不算分，不夠分就換下一段（CandidateVerifier）
//
// 誰呼叫它：App 的 MainWindow（按「掃描精華」）和 CLI 的 scan 指令。
// =====================================================================================

/// <summary>
/// Scans a recording for on-screen prompts (Apex: knock / elimination / squad wipe / kill leader / champion;
/// Minecraft: death screen / advancement toasts / raid victory / the player's kills in chat) using Windows OCR on the whole frame, so neither HUD position nor game language is assumed.
/// </summary>
public sealed class Scanner
{
    private readonly KeywordConfig _cfg;

    public Scanner(KeywordConfig cfg) => _cfg = cfg;

    // Pair = 「一個 OCR 引擎」配「一個語言的關鍵字規則」。例如 en@en-US、zh-Hant@zh-Hant-TW。
    // 還不知道遊戲語言時，每張畫面會用所有 Pair 都試一遍。
    private sealed record Pair(string Key, string OcrTag, KeywordMatcher Matcher, bool Native);
    // 一張畫面用某個 Pair 讀到的結果：命中的提示、是不是在觀戰（觀戰時別人的擊殺不算）、HUD 固定文字數量、是不是整張都有讀。
    private sealed record FrameHits(double Time, string PairKey, List<PromptHit> Hits, bool Spectating, int Anchors, bool Full);

    public async Task<ScanResult> ScanAsync(MediaInfo info, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult { File = info.Path, Duration = info.Duration, Resolution = $"{info.Width}x{info.Height}", Game = _cfg.Game };
        if (_cfg.Game == Game.Minecraft && string.IsNullOrWhiteSpace(opt.PlayerName))
            result.Notes.Add(Loc.T("note.mcNoPlayer"));

        // 準備所有可能的語言組合（電腦有裝哪些 OCR 語言就用哪些）
        var pairs = BuildPairs(opt.Language, opt.PlayerName, result.Notes);
        if (pairs.Count == 0)
            throw new InvalidOperationException(Loc.T("err.noOcr"));
        // 上次掃描判斷出的遊戲語言：只要一點點證據就能勝出，不用每種語言都試很久
        var preferred = opt.PreferredLanguage is { } pl ? pairs.FirstOrDefault(p => p.Matcher.LanguageId == pl) : null;

        // 音量分析在背景同時跑，最後用來估計交戰從哪裡開始（槍聲變大的地方）
        var audioTask = AudioActivity.AnalyzeAsync(info, opt.AudioTrack, ct);

        // Language lock: before lock every frame is OCR'd with every (engine, rules) pair; afterwards only the winner.
        Pair? locked = pairs.Count == 1 ? pairs[0] : null;
        bool forcedLock = false;
        var hitCount = pairs.ToDictionary(p => p.Key, _ => 0);
        var gate = new object();
        Pair Best() => pairs.OrderByDescending(p => hitCount[p.Key]).ThenByDescending(p => p == preferred).ThenByDescending(p => p.Native).First();

        // Region OCR: running box around every prompt read so far (fractions of the frame), in the locked language.
        double bx0 = 1, by0 = 1, bx1 = 0, by1 = 0;
        int boxHits = 0;
        FrameRegion? region = null;
        void Grow(IEnumerable<PromptHit> hs)
        {
            foreach (var h in hs)
            {
                bx0 = Math.Min(bx0, h.X); by0 = Math.Min(by0, h.Y);
                bx1 = Math.Max(bx1, h.X + h.W); by1 = Math.Max(by1, h.Y + h.H);
                boxHits++;
            }
            // Only trust the box once a few prompts agree, and only use it when it actually saves work.
            if (opt.RegionOcr && boxHits >= 3)
            {
                var r = FrameRegion.Around(bx0, by0, bx1, by1, 0.08, 0.06, 0.35, 0.18);
                region = r.Area < 0.6 ? r : null;
            }
        }

        // Channel 是一條「輸送帶」：一端放畫面（生產者），多個 worker 從另一端拿（消費者）。
        // Bounded = 輸送帶有長度上限，worker 來不及處理時，讀畫面那端會等，不會把記憶體塞爆。
        var channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(opt.Workers * 2) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var collected = new List<FrameHits>();
        int framesDone = 0;
        int totalFrames = Math.Max(1, (int)(info.Duration * opt.SampleFps));
        int fullEvery = Math.Max(1, opt.FullFrameEvery);

        var engineTags = pairs.Select(p => p.OcrTag).Distinct().ToList();

        // ② 開好幾個 worker 同時做 OCR（OCR 最慢，平行處理才快）
        var workers = Enumerable.Range(0, opt.Workers).Select(_ => Task.Run(async () =>
        {
            // 每個 worker 有自己的 OCR 引擎，避免互搶
            var readers = engineTags.ToDictionary(t => t, OcrReader.Create);
            await foreach (var f in channel.Reader.ReadAllAsync(ct))
            {
                List<FrameHits> local;
                try
                {
                    // lock：多個 worker 會同時讀寫 locked / collected，要排隊進去，不然資料會亂掉
                    Pair? lk;
                    FrameRegion? roi;
                    lock (gate) { lk = locked; roi = region; }
                    // 語言確定了就只跑那一組；還沒確定就全部試
                    var active = lk != null ? [lk] : pairs;
                    // 語言確定、也知道提示大概在哪之後，大部分畫面只讀那一塊；每隔幾張還是讀整張，才找得到出現在新位置的提示
                    bool full = lk == null || roi == null || f.Index % fullEvery == 0;
                    var linesByTag = new Dictionary<string, List<OcrTextLine>>();
                    foreach (var tag in active.Select(a => a.OcrTag).Distinct())
                        linesByTag[tag] = await readers[tag].ReadAsync(f.Bgra, f.Width, f.Height, full ? null : roi);

                    // ③ 每一行文字拿去比對關鍵字
                    local = new List<FrameHits>();
                    foreach (var pair in active)
                    {
                        var lines = linesByTag[pair.OcrTag];
                        bool spect = full && lines.Any(l => pair.Matcher.IsSpectating(l.Text));
                        int anchors = lk == null ? lines.Count(l => pair.Matcher.IsAnchor(l.Text)) : 0;
                        var hits = new List<PromptHit>();
                        foreach (var l in lines)
                        {
                            var m = pair.Matcher.Match(l.Text);
                            if (m is { } mm) hits.Add(new PromptHit(f.Time, mm.Kind, l.Text, mm.After, l.X, l.Y, l.W, l.H));
                        }
                        local.Add(new FrameHits(f.Time, pair.Key, hits, spect, anchors, full));
                    }
                }
                finally { f.Release(); }   // 畫面的記憶體還給 pool

                int done;
                List<PromptHit>? snap = null;
                lock (gate)
                {
                    collected.AddRange(local);
                    // ④ 判斷遊戲語言：某個語言累積命中 ≥ 6 次（上次的語言只要 2 次），而且是第二名的 2 倍以上，就鎖定它
                    if (locked == null)
                    {
                        // Prompts and always-on HUD text (e.g. "SQUADS LEFT") both count as evidence for the language.
                        foreach (var fh in local) if (!fh.Spectating) hitCount[fh.PairKey] += fh.Hits.Count + fh.Anchors;
                        var best = Best();
                        var second = pairs.Where(p => p != best).Select(p => hitCount[p.Key]).DefaultIfEmpty(0).Max();
                        int need = best == preferred ? 2 : 6;
                        if (hitCount[best.Key] >= need && hitCount[best.Key] >= second * 2) locked = best;
                        // 掃了很久還判斷不出來（幾乎沒有字的場次）→ 直接用目前最像的，不要整場都用每種語言讀
                        else if (opt.LanguageProbeFrames > 0 && framesDone + 1 >= opt.LanguageProbeFrames)
                        {
                            locked = hitCount[best.Key] == 0 && preferred != null ? preferred : best;
                            forcedLock = true;
                        }
                        if (locked != null)
                            Grow(collected.Where(c => c.PairKey == locked.Key && !c.Spectating).SelectMany(c => c.Hits));
                    }
                    else
                    {
                        foreach (var fh in local) if (fh.PairKey == locked.Key && !fh.Spectating) Grow(fh.Hits);
                    }
                    done = ++framesDone;
                    // 每 10 張回報一次進度（介面上的進度條和黃色刻度就是靠這個）
                    if (done % 10 == 0 && locked != null)
                    {
                        var key = locked.Key;
                        snap = collected.Where(c => c.PairKey == key && !c.Spectating).SelectMany(c => c.Hits).OrderBy(h => h.Time).ToList();
                    }
                }
                if (done % 10 == 0)
                {
                    Pair? lkNow;
                    lock (gate) lkNow = locked;
                    progress?.Report(new ScanProgress
                    {
                        Hits = snap,
                        Fraction = Math.Min(1, done / (double)totalFrames),
                        Message = Loc.F("progress.scanning", TimeFmt.Hms(f.Time), TimeFmt.Hms(info.Duration))
                                  + (lkNow != null ? Loc.F("progress.lang", lkNow.Matcher.LanguageId) : Loc.T("progress.detecting")),
                    });
                }
            }
        }, ct)).ToArray();

        // ① 生產者：FFmpeg 解出一張畫面就放上輸送帶
        try
        {
            await foreach (var frame in FrameReader.ReadAsync(info, opt.SampleFps, opt.OcrWidth, opt.HardwareDecode, ct))
                await channel.Writer.WriteAsync(frame, ct);
        }
        finally
        {
            channel.Writer.TryComplete();   // 告訴 worker：沒有新畫面了
        }
        await Task.WhenAll(workers);        // 等所有 worker 做完
        result.FramesScanned = framesDone;

        // Final language choice (may still be undecided if the match had almost no prompts).
        var winner = locked ?? Best();
        result.Language = winner.Matcher.LanguageId;
        result.OcrLanguage = winner.OcrTag;
        result.LanguageConfident = (locked != null && !forcedLock) || pairs.Count == 1;
        result.LanguageVerified = _cfg.Languages.First(l => l.Id == winner.Matcher.LanguageId).Verified;
        if (!result.LanguageConfident)
            result.Notes.Add(Loc.F("note.langUncertain", winner.Matcher.LanguageId, hitCount[winner.Key]));
        if (!result.LanguageVerified)
            result.Notes.Add(Loc.F("note.langUnverified", winner.Matcher.LanguageId, KeywordConfig.FileFor(_cfg.Game)));
        result.RegionFrames = collected.Count(c => c.PairKey == winner.Key && !c.Full);

        // 只留下勝出語言的結果，並丟掉觀戰中的畫面
        var mine = MarkSpectating(collected.Where(c => c.PairKey == winner.Key).OrderBy(c => c.Time).ToList());
        result.SpectatingFramesSkipped = mine.Count(m => m.Spectating);
        result.Hits = mine.Where(m => !m.Spectating).SelectMany(m => m.Hits).OrderBy(h => h.Time).ToList();
        if (MedianPosition(result.Hits) is { } pos) { result.PromptX = pos.X; result.PromptY = pos.Y; }

        // ⑤ 同一個提示被讀到好幾次 → 合併成一個事件
        result.Events = Deduplicate(result.Hits, opt.SampleFps);

        progress?.Report(new ScanProgress { Fraction = 1, Message = Loc.T("progress.merging") });
        AudioActivity? audio = null;
        try { audio = await audioTask; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Notes.Add(Loc.F("note.audioFailed", ex.Message)); }

        // ⑥⑦ 分群成交戰、算分、挑前 3 名
        result.Clusters = Cluster(result.Events, audio, info.Duration, opt);

        // ⑧ 二次確認：只被讀到幾次的提示用更高 fps 重讀，沒再看到就不算分；不夠分就換下一段（CandidateVerifier）
        if (opt.VerifyCandidates && result.Clusters.Count > 0)
        {
            progress?.Report(new ScanProgress { Fraction = 1, Message = Loc.T("progress.verifying") });
            FrameRegion? roi;
            lock (gate) roi = region;
            var reader = OcrReader.Create(winner.OcrTag);
            result.Clusters = await CandidateVerifier.SelectAsync(result.Clusters, opt, _cfg.Weight,
                c => RereadAsync(info, c, winner.Matcher, reader, roi, opt, result.Notes, ct), ct, _cfg.AlwaysKeeps);
            var checkedOnes = result.Clusters.Where(c => c.Check is { Rechecked: > 0, Reread: true }).ToList();
            if (checkedOnes.Count > 0)
                result.Notes.Add(Loc.F("note.verified", checkedOnes.Count, checkedOnes.Sum(c => c.Check!.Rechecked),
                    checkedOnes.Count(c => c.Reject == RejectKind.Unconfirmed), checkedOnes.Sum(c => c.Check!.Frames)));
        }
        result.Candidates = result.Clusters.Where(c => c.Accepted).ToList();
        result.ScanSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    // 二次確認用：把一段交戰裡讀得不夠確定的提示附近，用更高的 fps 重讀一次。讀不到畫面就回傳 null（保留原判斷）。
    private static async Task<(IReadOnlyList<PromptHit> Hits, int Frames)?> RereadAsync(MediaInfo info, Candidate c, KeywordMatcher matcher,
        OcrReader reader, FrameRegion? roi, ScanOptions opt, List<string> notes, CancellationToken ct)
    {
        var hits = new List<PromptHit>();
        int frames = 0;
        try
        {
            foreach (var (start, end) in CandidateVerifier.Windows(c, opt, info.Duration))
            {
                await foreach (var f in FrameReader.ReadAsync(info, opt.VerifyFps, opt.OcrWidth, opt.HardwareDecode, ct, start, end - start))
                {
                    try
                    {
                        foreach (var l in await reader.ReadAsync(f.Bgra, f.Width, f.Height, roi))
                            if (matcher.Match(l.Text) is { } m)
                                hits.Add(new PromptHit(start + f.Time, m.Kind, l.Text, m.After, l.X, l.Y, l.W, l.H));
                    }
                    finally { f.Release(); }
                    frames++;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            notes.Add(Loc.F("note.verifyFailed", TimeFmt.Hms(c.Start), ex.Message));
            return null;
        }
        return frames == 0 ? null : (hits, frames);
    }

    // 列出要試的「語言 × OCR 引擎」組合。
    // 如果某語言沒裝對應的 OCR（例如沒裝韓文），但它的關鍵字是英文字母，就借英文 OCR 來讀。
    private List<Pair> BuildPairs(string language, string? playerName, List<string> notes)
    {
        var installed = OcrReader.InstalledLanguages();
        var langs = language == "auto" ? _cfg.Languages : _cfg.Languages.Where(l => l.Id == language).ToList();
        if (langs.Count == 0) throw new ArgumentException(Loc.F("err.langMissing", language));

        var pairs = new List<Pair>();
        foreach (var l in langs)
        {
            var spect = _cfg.Spectate.TryGetValue(l.Id, out var s) ? s : [];
            var matcher = new KeywordMatcher(l, spect, playerName);
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

    /// <summary>How much of a death screen counts as part of the moment (it can stay up for minutes).</summary>
    internal const double DeathScreenSeconds = 3;

    // 去重複：4 秒內、同種類、被擊殺者名字相似（相似度 ≥ 55%）→ 視為同一個提示。
    // 例外：同一張畫面同時出現兩行「擊倒」而且名字明顯不同 → 是兩個人，分開算。
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
                bool stackable = h.Kind is EventKind.Knock or EventKind.Elimination or EventKind.PlayerKill;
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
        // The Minecraft death screen stays up until the player clicks Respawn; only its first seconds belong to the clip.
        return events.Select(e => new GameEvent(e.First, e.Kind == EventKind.Death ? Math.Min(e.Last, e.First + DeathScreenSeconds) : e.Last,
            e.Kind, e.Text.Trim(), e.N)).ToList();
    }

    // 把事件分成一場一場的交戰，並決定每段片段的起訖時間。
    //   分數 = 每個事件的權重相加（擊倒 1、淘汰 1.5、全隊淘汰 2.5…，在 keywords.json 設定）
    //          只被讀到一次的事件可信度低，算半分
    //   開始 = 從第一個提示往回找到聲音開始變吵的地方（限制在 8~25 秒前）；沒音軌就固定往前約 16 秒
    //   結束 = 最後一個提示 + 6 秒（成為冠軍再多 8 秒）
    //   太長（超過 MaxClipLength）→ 從中間最長的空檔切開；切不開就只留最短的片頭
    //   相鄰兩段重疊（第二段的片頭蓋到第一段的片尾）→ 在中間切開，兩段不會有重複畫面
    internal List<Candidate> Cluster(List<GameEvent> events, AudioActivity? audio, double duration, ScanOptions opt)
    {
        // 依時間排好，跟上一個事件相隔 ≤ 25 秒就放同一群
        var groups = new List<List<GameEvent>>();
        foreach (var e in events.OrderBy(e => e.Time))
        {
            if (groups.Count > 0 && e.Time - groups[^1][^1].LastSeen <= opt.MergeGap) groups[^1].Add(e);
            else groups.Add([e]);
        }
        if (opt.MaxClipLength > 0)
            groups = groups.SelectMany(g => SplitLong(g, opt)).ToList();

        var spans = new List<(List<GameEvent> G, double Start, double End)>();
        foreach (var g in groups)
        {
            double first = g[0].Time, last = g.Max(e => e.LastSeen);
            double start = audio != null
                ? audio.EstimateFightStart(first, opt.MinPreRoll, opt.MaxPreRoll)
                : Math.Max(0, first - (opt.MinPreRoll + opt.MaxPreRoll) / 2);
            double end = Math.Min(duration, last + PostRoll(g, opt, _cfg));
            // A fight that can't be split any further: keep only the minimum lead-in so it fits if it can.
            if (opt.MaxClipLength > 0 && end - start > opt.MaxClipLength)
                start = Math.Max(start, Math.Min(first - opt.MinPreRoll, end - opt.MaxClipLength));
            spans.Add((g, Math.Max(0, start), end));
        }

        // No two clips share footage: where one's lead-in reaches into the previous one's tail, cut in between
        // (never closer than 1 s to either fight's own prompts).
        for (int i = 1; i < spans.Count; i++)
        {
            var (pg, ps, pe) = spans[i - 1];
            var (g, s, e) = spans[i];
            if (s >= pe) continue;
            double prevLast = pg.Max(x => x.LastSeen), first = g[0].Time;
            double cut = Math.Clamp((s + pe) / 2, prevLast + 1, Math.Max(prevLast + 1, first - 1));
            spans[i - 1] = (pg, ps, Math.Min(pe, cut));
            spans[i] = (g, Math.Max(s, cut), e);
        }

        var all = new List<Candidate>();
        foreach (var (g, start, end) in spans)
        {
            double score = 0;
            foreach (var e in g)
                score += _cfg.Weight(e.Kind) * (e.Samples >= 2 ? 1.0 : 0.5);
            double first = g[0].Time, last = g.Max(e => e.LastSeen);

            // 不合格的情況：分數太低（但拿冠軍一定留）、只有助攻
            // 冠軍（Minecraft：完成挑戰、突襲勝利）一定留
            bool champion = g.Any(e => _cfg.AlwaysKeeps(e.Kind) && e.Samples >= 2);
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
        var keep = all.Where(c => c.Accepted).OrderByDescending(c => c.Score).ThenBy(c => c.Start).Take(Math.Max(0, opt.MaxCandidates)).ToHashSet();
        return all.Select(c => c.Accepted && !keep.Contains(c)
            ? new Candidate
            {
                Start = c.Start, End = c.End, FirstEvent = c.FirstEvent, LastEvent = c.LastEvent, Score = c.Score, Events = c.Events,
                LeadIn = c.LeadIn, LeadInFromAudio = c.LeadInFromAudio, Reject = RejectKind.NotTopN, Threshold = c.Threshold, TopN = c.TopN,
            }
            : c).OrderBy(c => c.Start).ToList();
    }

    private static double PostRoll(List<GameEvent> g, ScanOptions opt, KeywordConfig? cfg = null) =>
        opt.PostRoll + (g.Any(e => cfg?.AlwaysKeeps(e.Kind) ?? e.Kind == EventKind.Champion) ? 8 : 0);

    /// <summary>
    /// Splits a fight whose clip would be longer than MaxClipLength at its longest gap between prompts, recursively.
    /// The length is estimated with the maximum lead-in, so a part may come out a little shorter than the limit, never longer
    /// (apart from a single unbroken stretch of prompts, which is trimmed in <see cref="Cluster"/>).
    /// </summary>
    internal static IEnumerable<List<GameEvent>> SplitLong(List<GameEvent> g, ScanOptions opt)
    {
        double Length(List<GameEvent> x) => x.Max(e => e.LastSeen) - x[0].Time + opt.MaxPreRoll + PostRoll(x, opt);
        if (g.Count < 2 || Length(g) <= opt.MaxClipLength) { yield return g; yield break; }
        int cut = 1;
        double best = double.MinValue;
        for (int i = 1; i < g.Count; i++)
        {
            double gap = g[i].Time - g.Take(i).Max(e => e.LastSeen);
            if (gap > best) { best = gap; cut = i; }
        }
        foreach (var part in SplitLong(g.Take(cut).ToList(), opt)) yield return part;
        foreach (var part in SplitLong(g.Skip(cut).ToList(), opt)) yield return part;
    }

    /// <summary>
    /// "Spectating" text can only be seen on fully read frames, so a partly read frame takes the state of the nearest fully read one.
    /// </summary>
    private static List<FrameHits> MarkSpectating(List<FrameHits> frames)
    {
        var fullTimes = frames.Where(f => f.Full).Select(f => (f.Time, f.Spectating)).ToList();
        if (fullTimes.Count == 0) return frames;
        var res = new List<FrameHits>(frames.Count);
        int j = 0;
        foreach (var f in frames)
        {
            if (f.Full) { res.Add(f); continue; }
            while (j + 1 < fullTimes.Count && Math.Abs(fullTimes[j + 1].Time - f.Time) <= Math.Abs(fullTimes[j].Time - f.Time)) j++;
            res.Add(f with { Spectating = fullTimes[j].Spectating });
        }
        return res;
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

// 時間格式小工具：秒數 ↔ "01:23"、"0:01:23.4"。全專案共用。
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

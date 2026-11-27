namespace ApexHighlighter.Core;

// =====================================================================================
// CandidateVerifier.cs — 候選片段的「二次確認」（產出 → 驗收 → 重做）
//
// 掃描每秒只取 2 張畫面，有些提示只被讀到 1、2 次，可能是 OCR 看錯。
// 所以在決定要建議哪幾段之前，再看一眼：
//   ① 照分數高到低，一段一段檢查
//   ② 只被讀到很少次的提示（< VerifyTrustSamples），在它附近用更高的 fps（預設每秒 6 張）重讀
//   ③ 重讀時又看到同種提示好幾張（≥ VerifyMinFrames）→ 確認；沒看到 → 這個提示不算分
//   ④ 確認後的分數還夠 → 建議；不夠 → 標成「二次確認沒過」，換下一段來檢查（重做）
//   ⑤ 原本差一點沒選上的交戰，如果弱提示被確認後分數夠了，也會被補上
// 讀不到畫面（例如 FFmpeg 出錯）時保留原本的判斷，不因為檢查失敗就丟掉片段。
//
// 真正讀畫面的部分在 Scanner（要用掃描時選好的語言和 OCR 引擎），這裡只有判斷邏輯，可以直接做單元測試。
// =====================================================================================

public static class CandidateVerifier
{
    /// <summary>Events that are weakly seen and should be read again.</summary>
    public static List<GameEvent> WeakEvents(Candidate c, ScanOptions opt) =>
        c.Events.Where(e => e.Samples < opt.VerifyTrustSamples).ToList();

    /// <summary>Time ranges to read again: around each weak event, overlapping ranges merged.</summary>
    public static List<(double Start, double End)> Windows(Candidate c, ScanOptions opt, double duration)
    {
        var res = new List<(double Start, double End)>();
        foreach (var e in WeakEvents(c, opt).OrderBy(e => e.Time))
        {
            double s = Math.Max(0, e.Time - 1), t = Math.Min(duration, e.LastSeen + 2.5);
            if (t <= s) continue;
            if (res.Count > 0 && s <= res[^1].End) res[^1] = (res[^1].Start, Math.Max(res[^1].End, t));
            else res.Add((s, t));
        }
        return res;
    }

    /// <summary>Whether a weak event's prompt was read again in enough frames (distinct times) near where it was first seen.</summary>
    public static bool IsConfirmed(GameEvent e, IReadOnlyList<PromptHit> dense, ScanOptions opt) =>
        dense.Where(h => h.Kind == e.Kind && h.Time >= e.Time - 1 && h.Time <= e.LastSeen + 2.5)
             .Select(h => h.Time).Distinct().Count() >= opt.VerifyMinFrames;

    /// <summary>Scores a fight again using only events that are strong or were read again.</summary>
    public static (CandidateCheck Check, List<GameEvent> Confirmed) Judge(Candidate c, IReadOnlyList<PromptHit> dense, int frames,
        Func<EventKind, double> weight, ScanOptions opt)
    {
        var weak = WeakEvents(c, opt).ToHashSet();
        var confirmed = c.Events.Where(e => !weak.Contains(e) || IsConfirmed(e, dense, opt)).ToList();
        double score = confirmed.Sum(e => weight(e.Kind));
        return (new CandidateCheck(weak.Count, confirmed.Count, c.Events.Count, score, frames, true), confirmed);
    }

    /// <summary>
    /// Picks the suggested fights with a second look. <paramref name="reread"/> reads the weak prompts of one fight again and
    /// returns what it saw plus how many frames it read, or null if it could not read them.
    /// Changes <see cref="Candidate.Reject"/> and <see cref="Candidate.Check"/> in place and returns the list in time order.
    /// </summary>
    public static async Task<List<Candidate>> SelectAsync(List<Candidate> clusters, ScanOptions opt, Func<EventKind, double> weight,
        Func<Candidate, Task<(IReadOnlyList<PromptHit> Hits, int Frames)?>> reread, CancellationToken ct)
    {
        // Near misses whose score could reach the threshold if their weak prompts are real get a second chance too.
        double Upside(Candidate c) => c.Events.Sum(e => weight(e.Kind));
        var pool = clusters
            .Where(c => c.Reject is RejectKind.None or RejectKind.NotTopN
                        || (c.Reject == RejectKind.LowScore && WeakEvents(c, opt).Count > 0 && Upside(c) >= opt.MinScore))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Start).ToList();

        int accepted = 0, checks = 0;
        int maxChecks = Math.Max(0, opt.MaxCandidates) + Math.Max(0, opt.VerifySpare);
        foreach (var c in pool)
        {
            ct.ThrowIfCancellationRequested();
            var original = c.Reject;
            bool wasSuggestible = original is RejectKind.None or RejectKind.NotTopN;
            if (accepted >= opt.MaxCandidates || checks >= maxChecks)
            {
                if (wasSuggestible) c.Reject = RejectKind.NotTopN;
                continue;
            }
            checks++;

            if (WeakEvents(c, opt).Count == 0)
            {
                // Every prompt was seen often enough already: nothing to recheck.
                c.Check = new CandidateCheck(0, c.Events.Count, c.Events.Count, c.Events.Sum(e => weight(e.Kind)), 0, true);
                if (wasSuggestible) { c.Reject = RejectKind.None; accepted++; }
                continue;
            }

            var read = await reread(c);
            if (read is not { } r)
            {
                // Could not look again: keep the first decision.
                c.Check = new CandidateCheck(WeakEvents(c, opt).Count, c.Events.Count, c.Events.Count, c.Score, 0, false);
                if (wasSuggestible) { c.Reject = RejectKind.None; accepted++; }
                continue;
            }

            var (check, confirmed) = Judge(c, r.Hits, r.Frames, weight, opt);
            c.Check = check;
            bool champion = confirmed.Any(e => e.Kind == EventKind.Champion);
            bool assistOnly = confirmed.Count > 0 && confirmed.All(e => e.Kind == EventKind.Assist);
            if ((check.ScoreAfter >= opt.MinScore || champion) && confirmed.Count > 0 && !assistOnly)
            {
                c.Reject = RejectKind.None;
                accepted++;
            }
            else c.Reject = original == RejectKind.LowScore ? RejectKind.LowScore : RejectKind.Unconfirmed;
        }
        return clusters.OrderBy(c => c.Start).ToList();
    }
}

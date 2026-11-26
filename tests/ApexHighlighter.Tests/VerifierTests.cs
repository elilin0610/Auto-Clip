using ApexHighlighter.Core;
using Xunit;
using static ApexHighlighter.Tests.TestData;

namespace ApexHighlighter.Tests;

// 二次確認（CandidateVerifier）和多支錄影流水線（Pipeline）。不需要 FFmpeg 或 OCR：重讀的結果用假資料代替。
// Second look (CandidateVerifier) and the multi-recording pipeline. No FFmpeg or OCR: re-reads are faked.
public class VerifierTests
{
    private static Candidate Fight(double start, RejectKind reject, params GameEvent[] events) => new()
    {
        Start = start, End = start + 40, FirstEvent = events[0].Time, LastEvent = events[^1].LastSeen, Events = events.ToList(),
        Score = events.Sum(e => Config.Weight(e.Kind) * (e.Samples >= 2 ? 1.0 : 0.5)), Reject = reject, Threshold = 2, TopN = 3,
    };

    private static List<PromptHit> Seen(EventKind k, double from, int frames, double fps = 6) =>
        Enumerable.Range(0, frames).Select(i => new PromptHit(from + i / fps, k, k.ToString(), "", 0.4, 0.7, 0.1, 0.02)).ToList();

    private static Func<Candidate, Task<(IReadOnlyList<PromptHit> Hits, int Frames)?>> Reread(Func<Candidate, IReadOnlyList<PromptHit>?> f, List<Candidate>? calls = null) =>
        c =>
        {
            calls?.Add(c);
            var hits = f(c);
            return Task.FromResult<(IReadOnlyList<PromptHit> Hits, int Frames)?>(hits == null ? null : (hits, 20));
        };

    [Fact]
    public async Task Strong_fights_are_kept_without_reading_again()
    {
        var c = Fight(100, RejectKind.None, Ev(110, EventKind.Knock, samples: 4), Ev(115, EventKind.Elimination, samples: 5));
        var calls = new List<Candidate>();
        var res = await CandidateVerifier.SelectAsync([c], Opt(), Config.Weight, Reread(_ => [], calls), default);
        Assert.Empty(calls);
        Assert.True(Assert.Single(res).Accepted);
        Assert.Equal(0, c.Check!.Rechecked);
    }

    [Fact]
    public async Task Weak_prompts_not_seen_again_drop_the_fight_and_the_next_one_is_checked()
    {
        // Two fights fill the top 2; the first one's prompts were each read only once and do not show up again.
        var ghost = Fight(100, RejectKind.None, Ev(110, EventKind.Elimination, samples: 1), Ev(112, EventKind.SquadWipe, samples: 1));
        var real = Fight(300, RejectKind.None, Ev(310, EventKind.Knock, samples: 4), Ev(312, EventKind.Elimination, samples: 4));
        var spare = Fight(500, RejectKind.NotTopN, Ev(510, EventKind.Knock, samples: 3), Ev(515, EventKind.Knock, samples: 3));
        var res = await CandidateVerifier.SelectAsync([ghost, real, spare], Opt(o => o.MaxCandidates = 2), Config.Weight, Reread(_ => []), default);

        Assert.Equal(RejectKind.Unconfirmed, ghost.Reject);
        Assert.Equal(0, ghost.Check!.Confirmed);
        Assert.NotNull(ghost.RejectReason);
        Assert.True(real.Accepted);
        Assert.True(spare.Accepted);   // redo: promoted in place of the dropped fight
        Assert.Equal(new[] { 100.0, 300, 500 }, res.Select(c => c.Start).ToArray());
    }

    [Fact]
    public async Task Weak_prompts_seen_again_keep_the_fight_at_full_weight()
    {
        var c = Fight(100, RejectKind.None, Ev(110, EventKind.Knock, samples: 1), Ev(113, EventKind.Elimination, samples: 2));
        await CandidateVerifier.SelectAsync([c], Opt(), Config.Weight,
            Reread(_ => [.. Seen(EventKind.Knock, 110, 8), .. Seen(EventKind.Elimination, 113, 8)]), default);
        Assert.True(c.Accepted);
        Assert.Equal(2, c.Check!.Confirmed);
        Assert.Equal(Config.Weight(EventKind.Knock) + Config.Weight(EventKind.Elimination), c.Check.ScoreAfter, 3);
    }

    [Fact]
    public async Task Near_miss_whose_weak_prompt_is_confirmed_is_promoted()
    {
        // Knock 1 + Knock×0.5 = 1.5 < 2, but if the weak knock is real the fight scores 2.
        var c = Fight(100, RejectKind.LowScore, Ev(110, EventKind.Knock, samples: 2), Ev(140, EventKind.Knock, samples: 1));
        Assert.True(c.Score < 2);
        await CandidateVerifier.SelectAsync([c], Opt(), Config.Weight,
            Reread(_ => [.. Seen(EventKind.Knock, 110, 6), .. Seen(EventKind.Knock, 140, 6)]), default);
        Assert.True(c.Accepted);
        Assert.Equal(2, c.Check!.ScoreAfter, 3);
    }

    [Fact]
    public async Task Near_miss_that_is_not_confirmed_stays_low_score()
    {
        var c = Fight(100, RejectKind.LowScore, Ev(110, EventKind.Knock, samples: 2), Ev(140, EventKind.Knock, samples: 1));
        await CandidateVerifier.SelectAsync([c], Opt(), Config.Weight,
            Reread(_ => [.. Seen(EventKind.Knock, 110, 6), .. Seen(EventKind.Knock, 140, 1)]), default);
        Assert.Equal(RejectKind.LowScore, c.Reject);
    }

    [Fact]
    public async Task If_frames_cannot_be_read_again_the_first_decision_is_kept()
    {
        var c = Fight(100, RejectKind.None, Ev(110, EventKind.Elimination, samples: 1), Ev(112, EventKind.SquadWipe, samples: 1));
        await CandidateVerifier.SelectAsync([c], Opt(), Config.Weight, Reread(_ => null), default);
        Assert.True(c.Accepted);
        Assert.False(c.Check!.Reread);
    }

    [Fact]
    public async Task No_more_than_max_plus_spare_fights_are_checked()
    {
        var fights = Enumerable.Range(0, 10).Select(i => Fight(i * 100, RejectKind.None, Ev(i * 100 + 10, EventKind.SquadWipe, samples: 1))).ToList();
        var calls = new List<Candidate>();
        await CandidateVerifier.SelectAsync(fights, Opt(o => { o.MaxCandidates = 3; o.VerifySpare = 2; }), Config.Weight, Reread(_ => [], calls), default);
        Assert.Equal(5, calls.Count);
        Assert.Equal(5, fights.Count(f => f.Reject == RejectKind.Unconfirmed));
        Assert.Equal(5, fights.Count(f => f.Reject == RejectKind.NotTopN));
    }

    [Fact]
    public void Reread_windows_cover_only_weak_prompts_and_merge_when_close()
    {
        var c = Fight(100, RejectKind.None, Ev(110, EventKind.Knock, samples: 1, last: 110), Ev(112, EventKind.Knock, samples: 1, last: 112),
            Ev(130, EventKind.Elimination, samples: 5), Ev(150, EventKind.Knock, samples: 1, last: 150));
        var w = CandidateVerifier.Windows(c, Opt(), 1000);
        Assert.Equal(2, w.Count);
        Assert.Equal((109.0, 114.5), w[0]);
        Assert.Equal((149.0, 152.5), w[1]);
    }
}

public class PipelineTests
{
    [Fact]
    public async Task Results_come_back_in_input_order_and_a_failure_only_affects_its_own_item()
    {
        var res = await Pipeline.RunAsync(new[] { 1, 2, 3, 4 },
            async (x, ct) => { await Task.Delay(10 * (5 - x), ct); if (x == 2) throw new InvalidOperationException("bad"); return x * 10; }, 2,
            async (x, mid, ct) => { await Task.Yield(); if (x == 4) throw new InvalidOperationException("export"); return $"ok {mid}"; }, 2,
            (x, ex) => $"error {x}: {ex.Message}", default);
        Assert.Equal(new[] { "ok 10", "error 2: bad", "ok 30", "error 4: export" }, res.ToArray());
    }

    [Fact]
    public async Task Second_station_works_on_one_item_while_the_first_station_handles_the_next()
    {
        // Item 2's scan waits until item 1's export has started. Without overlap this would time out.
        var exportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Pipeline.RunAsync(new[] { 1, 2 },
            async (x, ct) => { if (x == 2) await exportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); return x; }, 1,
            (x, mid, ct) => { if (x == 1) exportStarted.TrySetResult(); return Task.FromResult(mid); }, 1,
            (x, ex) => -1, default);
        Assert.Equal(new[] { 1, 2 }, (await run).ToArray());
    }
}

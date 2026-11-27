using ApexHighlighter.Core;
using Xunit;
using static ApexHighlighter.Tests.TestData;

namespace ApexHighlighter.Tests;

// 去重複、分群成交戰、算分、切太長的片段、避免片段重疊。不需要 FFmpeg 或 OCR。
public class ScannerTests
{
    private readonly Scanner _scanner = new(Config);

    [Fact]
    public void Accepted_scan_replays_to_the_same_events_and_clip()
    {
        var (duration, hits) = ScanB();
        var events = Scanner.Deduplicate(hits, 2);
        Assert.Equal(
            new[] { EventKind.Elimination, EventKind.Knock, EventKind.Elimination, EventKind.Knock, EventKind.Elimination, EventKind.SquadWipe, EventKind.KillLeader },
            events.Select(e => e.Kind).ToArray());

        var clips = _scanner.Cluster(events, null, duration, Opt());
        var c = Assert.Single(clips);
        Assert.True(c.Accepted);
        Assert.Equal(9.25, c.Score, 3);
        Assert.Equal(duration, c.End, 3);
    }

    [Fact]
    public void Same_prompt_over_several_samples_is_one_event()
    {
        var hits = new[] { 10.0, 10.5, 11.0, 11.5 }.Select(t => new PromptHit(t, EventKind.Knock, "KNOCKED DOWN WRAITH", "WRAITH", 0.4, 0.7, 0.1, 0.02)).ToList();
        var e = Assert.Single(Scanner.Deduplicate(hits, 2));
        Assert.Equal(4, e.Samples);
        Assert.Equal(10, e.Time);
        Assert.Equal(11.5, e.LastSeen);
    }

    [Fact]
    public void Two_knocks_on_screen_at_once_with_different_names_are_two_events()
    {
        var hits = new List<PromptHit>
        {
            new(10, EventKind.Knock, "KNOCKED DOWN WRAITH", "WRAITH", 0.4, 0.70, 0.1, 0.02),
            new(10, EventKind.Knock, "KNOCKED DOWN OCTANE", "OCTANE", 0.4, 0.73, 0.1, 0.02),
        };
        Assert.Equal(2, Scanner.Deduplicate(hits, 2).Count);
    }

    [Fact]
    public void Low_score_and_assist_only_fights_are_not_suggested()
    {
        var clips = _scanner.Cluster(
        [
            Ev(100, EventKind.Knock),                                             // 1.0 < 2
            Ev(300, EventKind.Assist), Ev(305, EventKind.Assist), Ev(310, EventKind.Assist), Ev(315, EventKind.Assist), Ev(320, EventKind.Assist),
        ], null, 1000, Opt());
        Assert.Equal(2, clips.Count);
        Assert.Equal(RejectKind.LowScore, clips[0].Reject);
        Assert.Equal(RejectKind.AssistOnly, clips[1].Reject);
    }

    [Fact]
    public void A_one_sample_event_counts_half()
    {
        var c = Assert.Single(_scanner.Cluster([Ev(100, EventKind.SquadWipe, samples: 1)], null, 1000, Opt()));
        Assert.Equal(1.25, c.Score, 3);
    }

    [Fact]
    public void Champion_is_always_kept()
    {
        var c = Assert.Single(_scanner.Cluster([Ev(100, EventKind.Champion)], null, 1000, Opt(o => o.MinScore = 100)));
        Assert.True(c.Accepted);
        Assert.Equal(101 + 6 + 8, c.End, 3);
    }

    [Fact]
    public void Only_the_best_N_are_kept_and_the_rest_say_why()
    {
        var events = new[] { 100.0, 300, 500, 700 }.Select((t, i) => Ev(t, i == 0 ? EventKind.Knock : EventKind.SquadWipe)).ToList();
        events.Insert(1, Ev(102, EventKind.Knock));
        var clips = _scanner.Cluster(events, null, 1000, Opt(o => o.MaxCandidates = 3));
        Assert.Equal(4, clips.Count);
        Assert.Equal(3, clips.Count(c => c.Accepted));
        Assert.Equal(RejectKind.NotTopN, clips[0].Reject);   // the 2-knock fight (2.0) loses to the squad wipes (2.5)
    }

    [Fact]
    public void Neighbouring_clips_never_overlap()
    {
        // 27 s apart → two fights; a 25 s lead-in would reach back into the first fight's tail.
        var clips = _scanner.Cluster([Ev(100, EventKind.SquadWipe), Ev(128, EventKind.SquadWipe)], null, 1000,
            Opt(o => { o.MinPreRoll = 25; o.MaxPreRoll = 25; }));
        Assert.Equal(2, clips.Count);
        Assert.True(clips[1].Start >= clips[0].End, $"{clips[0].End} > {clips[1].Start}");
        Assert.True(clips[0].End >= 101 + 1);    // the first fight keeps its own prompts
        Assert.True(clips[1].Start <= 128 - 1);  // and so does the second
        Assert.Equal(128 - clips[1].Start, clips[1].LeadIn, 3);
    }

    [Fact]
    public void Long_fights_are_split_at_the_longest_pause()
    {
        var times = new[] { 30.0, 50, 70, 90, 110, 134, 154, 174, 194, 214 };   // 20 s apart, 24 s in the middle
        var clips = _scanner.Cluster(times.Select(t => Ev(t, EventKind.Knock)).ToList(), null, 1000,
            Opt(o => { o.MaxClipLength = 120; o.MaxCandidates = 10; }));
        Assert.Equal(2, clips.Count);
        Assert.All(clips, c => Assert.True(c.End - c.Start <= 120, $"{c.End - c.Start:0.0} s"));
        Assert.Equal(111, clips[0].LastEvent);   // LastSeen of the 110 s prompt
        Assert.Equal(134, clips[1].FirstEvent);
    }

    [Fact]
    public void No_limit_keeps_one_long_fight()
    {
        var times = new[] { 30.0, 50, 70, 90, 110, 134, 154, 174, 194, 214 };
        var clips = _scanner.Cluster(times.Select(t => Ev(t, EventKind.Knock)).ToList(), null, 1000, Opt(o => o.MaxClipLength = 0));
        Assert.Single(clips);
    }

    [Fact]
    public void SplitLong_leaves_short_fights_alone()
    {
        var g = new List<GameEvent> { Ev(10, EventKind.Knock), Ev(20, EventKind.Knock) };
        Assert.Single(Scanner.SplitLong(g, Opt()));
    }
}

using ApexHighlighter.Core;
using Xunit;

namespace ApexHighlighter.Tests;

// 擊殺提示的判斷規則。正反例都來自 acceptance/驗收紀錄.md 裡實際錄影讀到的字。
public class KeywordTests
{
    private readonly KeywordMatcher _en = TestData.English();

    [Theory]
    [InlineData("KNOCKED DOWN WRAITH +150", EventKind.Knock)]
    [InlineData("GKNOCKEO DOWN", EventKind.Knock)]           // OCR noise, fuzzy match
    [InlineData("KN0CKED DOWN", EventKind.Knock)]            // 0 read for O
    [InlineData("e ELIMINATED", EventKind.Elimination)]
    [InlineData("COMEBACK KILL +300", EventKind.Elimination)]
    [InlineData("SQUAD WIPE +100", EventKind.SquadWipe)]
    [InlineData("YOU ARE THE NEW KILL LEADER", EventKind.KillLeader)]
    [InlineData("ASSIST, KNOCKED DOWN BLOODHOUND", EventKind.Assist)]
    [InlineData("ASSIST, ELIMINATION PATHFINDER", EventKind.Assist)]
    [InlineData("ENEMY SQUAD ELIMINATED +50", EventKind.Assist)]
    public void Recognizes_own_prompts(string line, EventKind kind)
    {
        Assert.Equal(kind, _en.Match(line)?.Kind);
    }

    [Theory]
    [InlineData("Team Eliminated +5 RP")]                    // ranked RP notice (mixed case)
    [InlineData("Announcer: Kill Leader eliminated.")]       // subtitle
    [InlineData("Knockdown Shield")]                         // item name
    [InlineData("KNOCKDOWN SHIELD")]
    [InlineData("SQUAD ELIMINATED")]                         // your own squad is out
    [InlineData("KNOCKED DOWN BY RAIDER")]                   // you were knocked
    [InlineData("SQUADS LEFT 12")]
    [InlineData("")]
    public void Ignores_everything_else(string line)
    {
        Assert.Null(_en.Match(line));
    }

    [Fact]
    public void Every_hit_of_the_accepted_scan_still_matches_its_kind()
    {
        var (_, hits) = TestData.ScanB();
        Assert.NotEmpty(hits);
        foreach (var h in hits)
            Assert.True(_en.Match(h.Text)?.Kind == h.Kind, $"'{h.Text}' should be {h.Kind}");
    }

    [Fact]
    public void Spectating_and_anchor_text()
    {
        Assert.True(_en.IsSpectating("SPECTATING Octane"));
        Assert.False(_en.IsSpectating("KNOCKED DOWN"));
        Assert.True(_en.IsAnchor("SQUADS LEFT 9"));
    }

    [Theory]
    [InlineData("KNOCKED DOWN", true)]
    [InlineData("KNOCKEO ooWN", true)]   // 9 of 11 letters upper
    [InlineData("Knockdown Shield", false)]
    [InlineData("ab", true)]             // too few letters to judge
    public void MostlyUpper(string s, bool expected) => Assert.Equal(expected, KeywordMatcher.MostlyUpper(s));

    [Fact]
    public void Norm_strips_spaces_punctuation_and_full_width()
    {
        Assert.Equal("KNOCKEDDOWN150", KeywordMatcher.Norm("ｋｎｏｃｋｅｄ  down, +150!"));
    }

    [Fact]
    public void Fuzzy_contains_reports_where_the_keyword_is()
    {
        Assert.True(KeywordMatcher.FuzzyContains("XXKNOCKEDDOWNYY", "KNOCKEDDOWN", out var s, out var e));
        Assert.Equal(2, s);
        Assert.Equal(13, e);
        Assert.False(KeywordMatcher.FuzzyContains("HELLOWORLD", "KNOCKEDDOWN", out _, out _));
        Assert.False(KeywordMatcher.FuzzyContains("擊倒了", "淘汰", out _, out _));   // CJK must match exactly
    }

    [Fact]
    public void Similarity()
    {
        Assert.Equal(1, KeywordMatcher.Similarity("WRAITH", "WRAITH"));
        Assert.True(KeywordMatcher.Similarity("WRAITH", "WRA1TH") > 0.8);
        Assert.True(KeywordMatcher.Similarity("WRAITH", "OCTANE") < 0.55);
    }
}

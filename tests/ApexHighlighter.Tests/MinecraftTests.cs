using ApexHighlighter.Core;
using Xunit;
using static ApexHighlighter.Tests.TestData;

namespace ApexHighlighter.Tests;

// Minecraft 的規則（keywords.minecraft.json）：死亡畫面、進度通知、突襲勝利、聊天欄裡玩家自己的擊殺。
// 文字是依遊戲語言檔寫的，還沒用實際錄影確認（verified=false）。
// Minecraft rules: death screen, advancement toasts, raid victory, and the player's own kills in chat.
public class MinecraftTests
{
    private static readonly KeywordConfig Mc = KeywordConfig.ForGame(Game.Minecraft);

    private static KeywordMatcher Matcher(string lang = "en", string? player = "Steve") =>
        new(Mc.Languages.First(l => l.Id == lang), [], player);

    [Fact]
    public void Each_game_loads_its_own_file()
    {
        Assert.Equal(Game.Minecraft, Mc.Game);
        Assert.Equal(Game.Apex, KeywordConfig.ForGame(Game.Apex).Game);
        Assert.Contains(KeywordConfig.ForGame(Game.Apex).Languages, l => l.Rules.Any(r => r.Kind == EventKind.Knock));
        Assert.DoesNotContain(Mc.Languages.SelectMany(l => l.Rules), r => r.Kind is EventKind.Knock or EventKind.Elimination);
    }

    [Theory]
    [InlineData("apex", Game.Apex)]
    [InlineData("Minecraft", Game.Minecraft)]
    [InlineData("mc", Game.Minecraft)]
    [InlineData("fortnite", null)]
    public void Parses_game_names(string s, Game? expected) => Assert.Equal(expected, KeywordConfig.ParseGame(s));

    [Theory]
    [InlineData("You Died!", EventKind.Death)]
    [InlineData("Game Over!", EventKind.Death)]
    [InlineData("Advancement Made!", EventKind.Advancement)]
    [InlineData("Goal Reached!", EventKind.Goal)]
    [InlineData("Challenge Complete!", EventKind.Challenge)]
    [InlineData("Raid - Victory", EventKind.RaidVictory)]
    [InlineData("Alex was slain by Steve", EventKind.PlayerKill)]
    [InlineData("Alex was shot by Steve using [Bow]", EventKind.PlayerKill)]
    [InlineData("Alex drowned whilst trying to escape Steve", EventKind.PlayerKill)]
    [InlineData("Alex was blown up by Steve", EventKind.PlayerKill)]
    public void Recognizes_english_prompts(string line, EventKind kind) => Assert.Equal(kind, Matcher().Match(line)?.Kind);

    [Theory]
    [InlineData("Steve was slain by Zombie")]                 // the player died: the death screen counts, not this line
    [InlineData("Steve was slain by Alex")]
    [InlineData("Alex was slain by Zombie")]                  // someone else's death
    [InlineData("Alex was slain by Bob")]
    [InlineData("Steve has made the advancement [Stone Age]")] // chat copy of the toast
    [InlineData("<Alex> you died lol")]                       // chat text
    [InlineData("Respawn")]
    [InlineData("")]
    public void Ignores_everything_else(string line) => Assert.Null(Matcher().Match(line));

    [Fact]
    public void Without_a_player_name_chat_kills_are_skipped()
    {
        Assert.Null(Matcher(player: null).Match("Alex was slain by Steve"));
        Assert.Equal(EventKind.Death, Matcher(player: null).Match("You Died!")?.Kind);
    }

    [Theory]
    [InlineData("zh-Hant", "你死了！", EventKind.Death)]
    [InlineData("zh-Hant", "Alex 被 Steve 殺死了", EventKind.PlayerKill)]
    [InlineData("zh-Hant", "突襲 - 勝利", EventKind.RaidVictory)]
    [InlineData("zh-Hans", "Alex 被 Steve 杀死了", EventKind.PlayerKill)]
    [InlineData("ja", "Alex は Steve に殺された", EventKind.PlayerKill)]
    [InlineData("ja", "死んでしまった！", EventKind.Death)]
    public void Recognizes_other_languages(string lang, string line, EventKind kind) => Assert.Equal(kind, Matcher(lang).Match(line)?.Kind);

    [Theory]
    [InlineData("zh-Hant", "Steve 被 殭屍 殺死了")]
    [InlineData("zh-Hant", "<Alex> 你死了")]
    [InlineData("ja", "Steve は ゾンビ に殺された")]
    public void Ignores_own_deaths_and_chat_in_other_languages(string lang, string line) => Assert.Null(Matcher(lang).Match(line));

    [Fact]
    public void A_long_death_screen_only_counts_its_first_seconds()
    {
        // Player sat on the death screen for 40 s before respawning.
        var hits = Enumerable.Range(0, 80).Select(i => new PromptHit(100 + i * 0.5, EventKind.Death, "You Died!", "", 0.4, 0.3, 0.2, 0.05)).ToList();
        var e = Assert.Single(Scanner.Deduplicate(hits, 2));
        Assert.Equal(100, e.Time, 3);
        Assert.Equal(100 + Scanner.DeathScreenSeconds, e.LastSeen, 3);
        Assert.Equal(80, e.Samples);
    }

    [Fact]
    public void A_death_alone_is_a_highlight()
    {
        var c = Assert.Single(new Scanner(Mc).Cluster([Ev(100, EventKind.Death)], null, 1000, Opt()));
        Assert.True(c.Accepted);
        Assert.Equal(2.0, c.Score, 3);
    }

    [Fact]
    public void A_single_advancement_is_not_enough()
    {
        var c = Assert.Single(new Scanner(Mc).Cluster([Ev(100, EventKind.Advancement)], null, 1000, Opt()));
        Assert.Equal(RejectKind.LowScore, c.Reject);
    }

    [Fact]
    public void Challenge_and_raid_victory_are_always_kept()
    {
        var scanner = new Scanner(Mc);
        foreach (var kind in new[] { EventKind.Challenge, EventKind.RaidVictory })
        {
            var c = Assert.Single(scanner.Cluster([Ev(100, kind)], null, 1000, Opt(o => o.MinScore = 100)));
            Assert.True(c.Accepted, kind.ToString());
            Assert.Equal(101 + 6 + 8, c.End, 3);
        }
    }

    [Fact]
    public void Apex_rules_do_not_always_keep_minecraft_kinds()
    {
        Assert.True(Config.AlwaysKeeps(EventKind.Champion));
        Assert.False(Config.AlwaysKeeps(EventKind.Challenge));
        Assert.True(Mc.AlwaysKeeps(EventKind.Challenge));
        Assert.False(Mc.AlwaysKeeps(EventKind.Champion));
    }

    [Fact]
    public void Player_name_changes_the_cache_fingerprint_but_empty_keeps_the_old_one()
    {
        var plain = ScanCache.Fingerprint(new ScanOptions(), Mc);
        Assert.Equal(plain, ScanCache.Fingerprint(new ScanOptions { PlayerName = "  " }, Mc));
        Assert.NotEqual(plain, ScanCache.Fingerprint(new ScanOptions { PlayerName = "Steve" }, Mc));
        Assert.NotEqual(plain, ScanCache.Fingerprint(new ScanOptions(), Config));
    }

    [Fact]
    public void Every_kind_and_game_has_a_translated_name()
    {
        var keys = Loc.All.Select(kv => kv.Key).ToHashSet();
        foreach (var k in Enum.GetValues<EventKind>()) Assert.Contains("kind." + k, keys);
        foreach (var g in Enum.GetValues<Game>()) Assert.Contains("game." + g, keys);
    }
}

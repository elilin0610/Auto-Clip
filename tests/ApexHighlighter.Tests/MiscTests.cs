using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApexHighlighter.Core;
using Xunit;

namespace ApexHighlighter.Tests;

public class ClipRangeTests
{
    [Fact]
    public void Start_never_passes_the_end()
    {
        Assert.Equal(49, ClipRange.ClampStart(80, 50, 600));
        Assert.Equal(0, ClipRange.ClampStart(-5, 50, 600));
        Assert.Equal(30, ClipRange.ClampStart(30, 50, 600));
    }

    [Fact]
    public void End_never_goes_before_the_start_or_past_the_recording()
    {
        Assert.Equal(31, ClipRange.ClampEnd(10, 30, 600));
        Assert.Equal(600, ClipRange.ClampEnd(900, 30, 600));
        Assert.Equal(45, ClipRange.ClampEnd(45, 30, 600));
    }
}

public class TimeFmtTests
{
    [Theory]
    [InlineData("40", 40)]
    [InlineData("1:05", 65)]
    [InlineData("0:01:05.5", 65.5)]
    [InlineData("1:00:00", 3600)]
    public void Parses(string s, double expected)
    {
        Assert.True(TimeFmt.TryParse(s, out var v));
        Assert.Equal(expected, v, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    public void Rejects(string s) => Assert.False(TimeFmt.TryParse(s, out _));

    [Fact]
    public void Formats()
    {
        Assert.Equal("01:05", TimeFmt.Hms(65));
        Assert.Equal("1:01:05", TimeFmt.Hms(3665));
        Assert.Equal("0:01:05.5", TimeFmt.HmsMs(65.5));
    }
}

public class FileQueueTests
{
    [Fact]
    public void Keeps_every_recording_in_order_once()
    {
        var q = new FileQueue();
        Assert.True(q.Enqueue(@"C:\rec\a.mkv"));
        Assert.True(q.Enqueue(@"C:\rec\b.mkv"));
        Assert.False(q.Enqueue(@"C:\REC\A.mkv"));   // same file again
        Assert.Equal(2, q.Count);
        Assert.True(q.TryDequeue(out var first));
        Assert.Equal(@"C:\rec\a.mkv", first);
        Assert.True(q.TryDequeue(out var second));
        Assert.Equal(@"C:\rec\b.mkv", second);
        Assert.False(q.TryDequeue(out _));
    }
}

public class FrameRegionTests
{
    [Fact]
    public void Grows_by_the_margin_and_stays_inside_the_frame()
    {
        var r = FrameRegion.Around(0.40, 0.70, 0.50, 0.73, 0.08, 0.06, 0.35, 0.18);
        Assert.True(r.X <= 0.40 - 0.08 + 1e-9 && r.X + r.W >= 0.50 + 0.08 - 1e-9);
        Assert.True(r.W >= 0.35 - 1e-9 && r.H >= 0.18 - 1e-9);
        var edge = FrameRegion.Around(0.95, 0.95, 0.99, 0.99, 0.08, 0.06, 0.35, 0.18);
        Assert.True(edge.X >= 0 && edge.Y >= 0 && edge.X + edge.W <= 1 + 1e-9 && edge.Y + edge.H <= 1 + 1e-9);
    }
}

public class ObsTests
{
    [Fact]
    public void Auth_string_matches_the_obs_websocket_5_example()
    {
        // Example values from the obs-websocket 5 protocol documentation.
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=",
            ObsWatcher.AuthString("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="));
    }

    [Fact]
    public void Only_a_stopped_recording_gives_a_path()
    {
        JsonNode Ev(string state, string? path = @"C:\rec\a.mkv") => new JsonObject
        {
            ["op"] = 5,
            ["d"] = new JsonObject
            {
                ["eventType"] = "RecordStateChanged",
                ["eventData"] = new JsonObject { ["outputActive"] = false, ["outputState"] = state, ["outputPath"] = path },
            },
        };
        Assert.Equal(@"C:\rec\a.mkv", ObsWatcher.RecordingPathFromEvent(Ev("OBS_WEBSOCKET_OUTPUT_STOPPED")));
        Assert.Null(ObsWatcher.RecordingPathFromEvent(Ev("OBS_WEBSOCKET_OUTPUT_STARTED")));
        Assert.Null(ObsWatcher.RecordingPathFromEvent(Ev("OBS_WEBSOCKET_OUTPUT_STOPPED", null)));
        Assert.Null(ObsWatcher.RecordingPathFromEvent(JsonNode.Parse("""{"op":7,"d":{}}""")!));
    }
}

public class ScanCacheTests
{
    [Fact]
    public void Round_trips_and_ignores_a_changed_file()
    {
        var dir = Directory.CreateTempSubdirectory("apexhl-test").FullName;
        try
        {
            var video = Path.Combine(dir, "rec.mkv");
            File.WriteAllBytes(video, new byte[100]);
            var c = new Candidate
            {
                Start = 10, End = 50, FirstEvent = 20, LastEvent = 44, Score = 3.5, LeadIn = 10,
                Events = [new GameEvent(20, 21, EventKind.Knock, "KNOCKED DOWN", 3)],
            };
            var result = new ScanResult
            {
                File = video, Duration = 600, Language = "en", LanguageConfident = true,
                Events = c.Events, Clusters = [c], Candidates = [c],
            };
            Assert.NotNull(ScanCache.Save(result, dir));

            var back = ScanCache.TryLoad(video, dir);
            Assert.NotNull(back);
            var r = back!.Value.Result;
            Assert.Equal("en", r.Language);
            var bc = Assert.Single(r.Candidates);
            Assert.Equal(10, bc.Start);
            Assert.Equal(3.5, bc.Score);
            Assert.True(bc.Accepted);
            Assert.Equal(EventKind.Knock, Assert.Single(bc.Events).Kind);

            File.AppendAllText(video, "changed");
            Assert.Null(ScanCache.TryLoad(video, dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class LocTests
{
    [Fact]
    public void Every_string_has_all_languages_and_the_same_placeholders()
    {
        foreach (var (key, row) in Loc.All)
        {
            Assert.True(row.Length == Loc.Languages.Count, $"{key}: {row.Length} columns");
            var want = Placeholders(row[0]);
            for (int i = 1; i < row.Length; i++)
                if (!string.IsNullOrEmpty(row[i]))
                    Assert.True(want.SetEquals(Placeholders(row[i])), $"{key} [{Loc.Languages[i].Code}] placeholders differ");
        }
    }

    private static HashSet<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).ToHashSet();
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ApexHighlighter.Core;
using Xunit;

namespace ApexHighlighter.Tests;

// 實機測試發現的介面問題：縮短片段後的觸發原因、OBS 連線失敗的提示與逾時。
// UI fixes from hands-on testing: trigger reasons after trimming, OBS connection errors and timeouts.

public class TrimmedReasonTests
{
    private static Candidate Fight() => new()
    {
        Start = 10, End = 60,
        Events =
        [
            new GameEvent(20, 21, EventKind.Knock, "knocked", 3),
            new GameEvent(35, 36, EventKind.Elimination, "eliminated", 3),
            new GameEvent(50, 51, EventKind.Knock, "knocked", 3),
        ],
    };

    [Fact]
    public void Only_events_inside_the_trimmed_range_are_listed()
    {
        var kept = Fight().EventsWithin(30, 45);
        Assert.Single(kept);
        Assert.Equal(EventKind.Elimination, kept[0].Kind);
    }

    [Fact]
    public void Untrimmed_range_keeps_every_event()
    {
        var c = Fight();
        Assert.Equal(3, c.EventsWithin(c.Start, c.End).Count);
    }

    [Fact]
    public void Range_with_no_prompts_is_empty()
    {
        Assert.Empty(Fight().EventsWithin(52, 60));
    }
}

public class ObsErrorTests
{
    [Fact]
    public void Localhost_is_dialled_as_ipv4()
    {
        Assert.Equal("127.0.0.1", ObsWatcher.DialHost("localhost"));
        Assert.Equal("127.0.0.1", ObsWatcher.DialHost(" LocalHost "));
        Assert.Equal("192.168.1.5", ObsWatcher.DialHost("192.168.1.5"));
    }

    [Fact]
    public void Errors_are_classified()
    {
        Assert.Equal(ObsFailure.NotReachable, ObsWatcher.Classify(new TimeoutException()));
        Assert.Equal(ObsFailure.NotReachable, ObsWatcher.Classify(new WebSocketException("x", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Equal(ObsFailure.NotObs, ObsWatcher.Classify(new WebSocketException(WebSocketError.NotAWebSocket)));
        Assert.Equal(ObsFailure.NeedsPassword, ObsWatcher.Classify(new ObsAuthException("x", passwordMissing: true)));
        Assert.Equal(ObsFailure.WrongPassword, ObsWatcher.Classify(new ObsAuthException("x")));
        Assert.Equal(ObsFailure.Other, ObsWatcher.Classify(new InvalidOperationException("x")));
    }

    [Fact]
    public void Friendly_text_tells_the_user_to_open_obs_instead_of_the_raw_message()
    {
        var text = ObsWatcher.FriendlyError(new WebSocketException("Unable to connect to the remote server"), "localhost", 4455);
        Assert.Contains("localhost:4455", text);
        Assert.Contains("Enable WebSocket server", text);
        Assert.DoesNotContain("Unable to connect", text);
    }

    [Fact]
    public async Task Test_connection_to_a_closed_port_fails_fast()
    {
        // Grab a free port and close it again so nothing is listening there.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ObsWatcher.TestAsync("localhost", port, null));
        sw.Stop();

        Assert.Equal(ObsFailure.NotReachable, ObsWatcher.Classify(ex));
        Assert.True(sw.Elapsed < ObsWatcher.TestTimeout + TimeSpan.FromSeconds(1), $"took {sw.Elapsed.TotalSeconds:0.0} s");
    }
}

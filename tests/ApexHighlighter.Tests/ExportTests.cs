using ApexHighlighter.Core;
using Xunit;

namespace ApexHighlighter.Tests;

// 匯出：畫布大小、濾鏡、聲音（單一音軌 / 混音 / 音量標準化）、批次匯出一段失敗繼續下一段。
public class ExportTests
{
    private static MediaInfo Info(int tracks) =>
        new("in.mkv", 600, 1920, 1080, 60, "h264", Enumerable.Range(0, tracks).Select(i => new AudioTrack(i, 2, null, null)).ToList());

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1920, 1200, 2134, 1200)]   // 16:10 → pad left/right
    [InlineData(2560, 1080, 2560, 1440)]   // ultrawide → pad top/bottom
    public void Canvas_is_16x9_without_scaling(int w, int h, int cw, int ch) => Assert.Equal((cw, ch), Exporter.Canvas(w, h));

    [Fact]
    public void Vertical_formats_are_1080x1920()
    {
        Assert.Equal((1080, 1920), Exporter.OutputSize(1920, 1080, ExportFormat.VerticalCrop));
        Assert.Equal((1080, 1920), Exporter.OutputSize(1920, 1080, ExportFormat.VerticalFit));
        Assert.Contains("pad=2134:1200", Exporter.VideoFilter(1920, 1200, ExportFormat.Landscape));
        Assert.DoesNotContain("pad=", Exporter.VideoFilter(1920, 1080, ExportFormat.Landscape));
    }

    [Fact]
    public void One_track_by_default()
    {
        var args = Exporter.BuildArgs(Info(3), 10, 40, "out.mp4", "setsar=1", "libx264", new ExportOptions { AudioTrack = 1 });
        Assert.Equal("0:a:1", After(args, "-map", 1));
        Assert.DoesNotContain("-filter_complex", args);
        Assert.Contains("aresample=async=1:first_pts=0", After(args, "-af"));
        Assert.DoesNotContain("loudnorm", string.Join(' ', args));
    }

    [Fact]
    public void Mixing_puts_every_track_into_one()
    {
        var args = Exporter.BuildArgs(Info(2), 10, 40, "out.mp4", "setsar=1", "libx264", new ExportOptions { MixAllTracks = true, NormalizeLoudness = true });
        var fc = After(args, "-filter_complex");
        Assert.StartsWith("[0:a:0][0:a:1]amix=inputs=2", fc);
        Assert.Contains("loudnorm=I=-14", fc);
        Assert.EndsWith("[aout]", fc);
        Assert.Equal("[aout]", After(args, "-map", 1));
        Assert.DoesNotContain("-af", args);
    }

    [Fact]
    public void Mixing_one_track_is_just_that_track()
    {
        var (fc, af, map) = Exporter.AudioPlan(1, new ExportOptions { MixAllTracks = true });
        Assert.Null(fc);
        Assert.NotNull(af);
        Assert.Equal("0:a:0", map);
    }

    [Fact]
    public void No_audio_tracks_no_audio_arguments()
    {
        var args = Exporter.BuildArgs(Info(0), 10, 40, "out.mp4", "setsar=1", "libx264", new ExportOptions { MixAllTracks = true });
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-filter_complex", args);
    }

    [Fact]
    public async Task A_failing_clip_does_not_stop_the_others()
    {
        var jobs = new[] { new ExportJob(1, 0, 10), new ExportJob(2, 20, 30), new ExportJob(3, 40, 50) };
        var ran = new List<int>();
        var ok = new ExportCheck(true, 10, 10, 10, 0, 0, 1920, 1080, "ok");
        var results = await ExportBatch.RunAsync(jobs, (job, _, _) =>
        {
            ran.Add(job.Number);
            if (job.Number == 2) throw new InvalidOperationException("disk full");
            return Task.FromResult(($"{job.Number}.mp4", ok));
        }, null, null, null, CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, ran);
        Assert.True(results[0].Ok);
        Assert.False(results[1].Ok);
        Assert.Equal("disk full", results[1].Error!.Message);
        Assert.True(results[2].Ok);
    }

    [Fact]
    public async Task Cancel_stops_the_batch()
    {
        using var cts = new CancellationTokenSource();
        var jobs = new[] { new ExportJob(1, 0, 10), new ExportJob(2, 20, 30) };
        var ran = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportBatch.RunAsync(jobs, (_, _, ct) =>
        {
            ran++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(("x", new ExportCheck(true, 0, 0, 0, 0, 0, 0, 0, "")));
        }, null, null, null, cts.Token));
        Assert.Equal(1, ran);
    }

    private static string After(List<string> args, string flag, int occurrence = 0)
    {
        int seen = 0;
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && seen++ == occurrence) return args[i + 1];
        throw new Xunit.Sdk.XunitException($"{flag} #{occurrence} not in: {string.Join(' ', args)}");
    }
}

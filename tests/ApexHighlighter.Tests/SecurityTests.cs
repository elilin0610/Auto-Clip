using ApexHighlighter.Core;
using Xunit;

namespace ApexHighlighter.Tests;

// 資安相關的檢查：OBS 傳來的路徑、OBS 位址、FFmpeg 的搜尋路徑與版本判斷
// Security checks: paths from OBS, the OBS host, FFmpeg search paths and version parsing.

public class ObsPathTests
{
    private static bool Exists(string _) => true;

    [Theory]
    [InlineData(@"C:\Videos\2026-11-27 01-02-03.mkv")]
    [InlineData(@"D:/Rec/match.mp4")]
    [InlineData(@"E:\OBS\clip.MOV")]
    public void Local_video_files_are_accepted(string path) =>
        Assert.True(ObsWatcher.IsAcceptableRecordingPath(path, Exists));

    [Fact]
    public void Null_is_rejected() => Assert.False(ObsWatcher.IsAcceptableRecordingPath(null, Exists));

    [Theory]
    [InlineData("")]
    [InlineData("http://evil.example/a.mkv")]
    [InlineData("file:///C:/Users/me/a.mkv")]
    [InlineData(@"\\server\share\a.mkv")]
    [InlineData(@"\\?\C:\a.mkv")]
    [InlineData(@"relative\a.mkv")]
    [InlineData(@"C:\Users\me\Documents\passwords.txt")]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData("concat:C:\\a.mkv|C:\\b.mkv")]
    public void Anything_else_is_rejected(string path) =>
        Assert.False(ObsWatcher.IsAcceptableRecordingPath(path, Exists));

    [Fact]
    public void Missing_files_are_rejected() =>
        Assert.False(ObsWatcher.IsAcceptableRecordingPath(@"C:\Videos\gone.mkv", _ => false));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("streampc.local", false)]
    public void Loopback_hosts_are_recognised(string host, bool loopback) =>
        Assert.Equal(loopback, ObsWatcher.IsLoopbackHost(host));
}

public class FFmpegSafetyTests
{
    [Fact]
    public void Relative_path_entries_are_skipped()
    {
        var dirs = FFmpegLocator.PathCandidates(@".;bin;C:\ffmpeg\bin;""C:\Program Files\x"";;  ;..\tools").ToList();
        Assert.Equal(new[] { @"C:\ffmpeg\bin", @"C:\Program Files\x" }, dirs);
    }

    [Theory]
    [InlineData("ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024", "7.1.0", false)]
    [InlineData("ffmpeg version n7.1.1-20-g1234 Copyright", "7.1.1", false)]
    [InlineData("ffmpeg version 6.0 Copyright", "6.0.0", false)]
    [InlineData("ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright", "4.4.2", true)]
    public void Release_versions_are_read(string line, string version, bool tooOld)
    {
        var v = FFmpegLocator.ParseVersion(line + "\nbuilt with gcc");
        Assert.Equal(Version.Parse(version), v.Release);
        Assert.Equal(tooOld, v.TooOld);
        Assert.False(v.Unknown);
    }

    [Fact]
    public void Git_builds_use_their_date()
    {
        var v = FFmpegLocator.ParseVersion("ffmpeg version 2025-01-02-git-abcdef-full_build-www.gyan.dev Copyright");
        Assert.Equal(new DateOnly(2025, 1, 2), v.BuildDate);
        Assert.False(v.TooOld);
        Assert.True(FFmpegLocator.ParseVersion("ffmpeg version 2022-06-01-git-abc").TooOld);
    }

    [Fact]
    public void Nightlies_are_unknown_not_too_old()
    {
        var v = FFmpegLocator.ParseVersion("ffmpeg version N-118000-g0123abcd Copyright");
        Assert.True(v.Unknown);
        Assert.False(v.TooOld);
    }
}

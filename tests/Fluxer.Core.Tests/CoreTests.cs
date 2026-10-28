using System.Diagnostics;
using System.Net.Http.Headers;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Platform.Windows;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class SegmentPlannerTests
{
    [Fact]
    public void Take_PrefersIdleSegment_OverSplitting()
    {
        var segs = new List<Segment> { new(0, 0, 100) { Active = true }, new(1, 100, 200) };
        var s = SegmentPlanner.Take(segs, allowSplit: true, minSplit: 10);
        Assert.Same(segs[1], s);
        Assert.True(s!.Active);
    }

    [Fact]
    public void Take_SplitsLargestActiveSegment_AtMidpointOfRemaining()
    {
        var a = new Segment(0, 0, 1000) { Active = true, Position = 200 };
        var b = new Segment(1, 1000, 1300) { Active = true, Position = 1000 };
        var segs = new List<Segment> { a, b };

        var piece = SegmentPlanner.Take(segs, true, 10)!;

        Assert.Equal(600, piece.Start);   // 200 + (800 / 2)
        Assert.Equal(1000, piece.End);
        Assert.Equal(600, a.End);
        Assert.Equal(2, piece.Index);
        Assert.Equal(3, segs.Count);
    }

    [Fact]
    public void Take_ReturnsNull_WhenRemainderTooSmallOrSplitDisallowed()
    {
        var segs = new List<Segment> { new(0, 0, 100) { Active = true, Position = 90 } };
        Assert.Null(SegmentPlanner.Take(segs, true, minSplit: 10));
        Assert.Null(SegmentPlanner.Take([new(0, 0, 1000) { Active = true }], allowSplit: false, minSplit: 10));
    }

    [Fact]
    public void Take_NeverSplitsUnknownLength()
    {
        Assert.Null(SegmentPlanner.Take([new(0, 0, long.MaxValue) { Active = true }], true, 10));
    }
}

public sealed class TokenBucketTests
{
    [Fact]
    public async Task Limits_Throughput_ToConfiguredRate()
    {
        var bucket = new TokenBucket { BytesPerSecond = 100_000 };
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 30; i++) await bucket.AcquireAsync(10_000, CancellationToken.None); // 300 KB
        // first second is burst capacity, the remaining 200 KB need ~2 s
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.6, 3.5);
    }

    [Fact]
    public async Task Zero_MeansUnlimited()
    {
        var bucket = new TokenBucket();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) await bucket.AcquireAsync(1 << 20, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 200);
    }
}

public sealed class FileNameTests
{
    [Theory]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\Windows\\evil.dll", "evil.dll")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.tar.gz", "_com1.tar.gz")]
    [InlineData("name. . .", "name")]
    [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h")]
    [InlineData("", "download")]
    [InlineData("...", "download")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Fact]
    public void Sanitize_TruncatesLongNames_KeepingExtension()
    {
        var name = FileNameSanitizer.Sanitize(new string('x', 400) + ".zip");
        Assert.True(name.Length <= 200);
        Assert.EndsWith(".zip", name);
    }

    [Fact]
    public void MakeUnique_SkipsExistingFilesAndParts()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "a.zip"), "");
        File.WriteAllText(Path.Combine(dir, "a (1).zip.part"), "");
        Assert.Equal("a (2).zip", FileNameSanitizer.MakeUnique(dir, "a.zip"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ReserveUnique_GivesDownloadsStartingTogetherDifferentNames()
    {
        // A batch of "download.php?id=…" links: all start at once and all want the same name.
        var dir = Directory.CreateTempSubdirectory().FullName;
        var names = new string[16];
        Parallel.For(0, names.Length, i => names[i] = FileNameSanitizer.ReserveUnique(dir, "dl.php"));
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.True(File.Exists(Path.Combine(dir, n + ".part")), n));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Resolver_PrefersFilenameStar_Utf8()
    {
        var cd = ContentDispositionHeaderValue.Parse("attachment; filename=\"fallback.txt\"; filename*=UTF-8''%C3%A7i%C3%A7ek%20d%C3%BC%C5%9F.pdf");
        Assert.Equal("çiçek düş.pdf", FileNameResolver.Resolve(cd, new Uri("https://x/y"), null));
    }

    [Fact]
    public void Resolver_FallsBackToUrl_ThenMime()
    {
        Assert.Equal("my file.iso", FileNameResolver.Resolve(null, new Uri("https://x/dl/my%20file.iso?sig=1"), null));
        Assert.Equal("download.zip", FileNameResolver.Resolve(null, new Uri("https://x/"), "application/zip"));
    }
}

public sealed class MediaTests
{
    [Fact]
    public void ParseHash_FindsAssetLine_AndSingleHashFiles()
    {
        const string h1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string h2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var sums = $"{h1}  yt-dlp_x86.exe\n{h2}  yt-dlp.exe\n";
        Assert.Equal(h2, ToolManager.ParseHash(sums, "yt-dlp.exe"));
        Assert.Equal(h1, ToolManager.ParseHash(h1 + "\n", "deno.zip", singleAsset: true));
        Assert.Null(ToolManager.ParseHash(h1 + "\n", "yt-dlp.exe")); // a list must name the asset
        Assert.Null(ToolManager.ParseHash($"{h1}  other.exe\n{h2}  other2.exe", "yt-dlp.exe"));
        // deno publishes PowerShell Get-FileHash output
        var ps = $"\r\nAlgorithm : SHA256\r\nHash      : {h2.ToUpperInvariant()}\r\nPath      : D:\\a\\deno\\target\\release\\deno.zip\r\n";
        Assert.Equal(h2.ToUpperInvariant(), ToolManager.ParseHash(ps, "deno-x86_64-pc-windows-msvc.zip", singleAsset: true));
    }

    [Fact]
    public void Netscape_Format_MatchesYtDlpExpectations()
    {
        var text = Cookies.ToNetscape([new CookieEntry("SID", "v", ".example.com", "/", true, true, 1700000000.5)]);
        Assert.Contains("#HttpOnly_.example.com\tTRUE\t/\tTRUE\t1700000000\tSID\tv", text);
        Assert.Equal("a=1; b=2", Cookies.ToHeader([new("a", "1", "x", "/", false, false, null), new("b", "2", "x", "/", false, false, null)]));
    }

    [Theory]
    [InlineData("ERROR: [youtube] x: Sign in to confirm you're not a bot", "media-signin")]
    [InlineData("ERROR: Unsupported URL: https://x", "media-unsupported")]
    [InlineData("something else", "media-failed")]
    public void ErrorClassification(string stderr, string code) => Assert.Equal(code, MediaErrors.Classify(stderr));

    [Fact]
    public void FormatSpec()
    {
        Assert.Equal("bv*[height<=1080]+ba/b[height<=1080]", YtDlp.FormatFor(1080, false));
        Assert.Equal("ba/b", YtDlp.FormatFor(null, true));
    }
}

public sealed class ProbeClassifyTests
{
    [Theory]
    [InlineData("application/x-bittorrent", "anything", ProbeKind.Torrent)]
    [InlineData("application/octet-stream", "d8:announce35:udp://x", ProbeKind.Torrent)]
    [InlineData(null, "d4:infod6:lengthi1ee", ProbeKind.Torrent)]
    [InlineData("text/html", "whatever", ProbeKind.Html)]
    [InlineData("application/octet-stream", "  <!DOCTYPE html><html>", ProbeKind.Html)]
    [InlineData("application/octet-stream", "<html><head>", ProbeKind.Html)]
    [InlineData("application/zip", "PK\u0003\u0004", ProbeKind.File)]
    [InlineData("application/octet-stream", "d", ProbeKind.File)]
    public void Classify(string? mime, string head, ProbeKind expected) =>
        Assert.Equal(expected, HttpProbe.Classify(mime, System.Text.Encoding.UTF8.GetBytes(head)));

    [Fact]
    public void NamesAFile()
    {
        Assert.True(FileNameResolver.NamesAFile(new Uri("https://x/a/setup.EXE?x=1")));
        Assert.True(FileNameResolver.NamesAFile(new Uri("https://x/t/ubuntu.torrent")));
        Assert.False(FileNameResolver.NamesAFile(new Uri("https://x/download/f3a872698cdea65a0f056f79cc6e0362")));
        Assert.False(FileNameResolver.NamesAFile(new Uri("https://x/get.php?id=1")));
    }
}

public sealed class SpeedProfileTests
{
    [Fact]
    public void UseProfile_CopiesLimitsAndSlots()
    {
        var s = new Downloads.AppSettings();
        s.UseProfile(2);
        Assert.Equal(2, s.ActiveProfile);
        Assert.Equal(2 * 1024 * 1024, s.HttpLimit);
        Assert.Equal(s.HttpLimit, s.TorrentDownloadLimit);
        Assert.Equal(200 * 1024, s.TorrentUploadLimit);
        Assert.Equal(1, s.HttpSlots);
        s.UseProfile(9); // out of range clamps to the last profile
        Assert.Equal(2, s.ActiveProfile);
    }

    [Fact]
    public void ScheduledProfile_ReadsMondayFirstHourCells()
    {
        var s = new Downloads.AppSettings { WeekScheduleOn = true };
        Assert.Equal(168, s.WeekSchedule.Length);
        Assert.Equal(0, s.ScheduledProfile(new DateTime(2026, 10, 12, 3, 0, 0)));  // Monday 03:00: night
        Assert.Equal(2, s.ScheduledProfile(new DateTime(2026, 10, 16, 10, 30, 0))); // Friday 10:30: office hours
        Assert.Equal(1, s.ScheduledProfile(new DateTime(2026, 10, 18, 10, 0, 0)));  // Sunday 10:00: balanced
        s.WeekSchedule = string.Concat(Enumerable.Repeat("1", 167)) + "2";
        Assert.Equal(2, s.ScheduledProfile(new DateTime(2026, 10, 18, 23, 0, 0)));  // Sunday 23:00 is the last cell
        s.WeekScheduleOn = false;
        Assert.Null(s.ScheduledProfile(DateTime.Now));
        s.WeekScheduleOn = true;
        s.WeekSchedule = "broken";
        Assert.Null(s.ScheduledProfile(DateTime.Now));
    }

    [Fact]
    public void UpgradeDesign_RunsOnce()
    {
        var s = new Downloads.AppSettings { Theme = "Light", ScheduleStart = null };
        s.Ui.Backdrop = "neu";
        Assert.True(s.UpgradeDesign());
        Assert.Equal(("Dark", "none", "01:00"), (s.Theme, s.Ui.Backdrop, s.ScheduleStart));
        s.Theme = "Light";
        Assert.False(s.UpgradeDesign());
        Assert.Equal("Light", s.Theme);
    }
}

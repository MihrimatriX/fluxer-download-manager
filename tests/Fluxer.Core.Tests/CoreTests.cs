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
        Assert.Equal(h1, ToolManager.ParseHash(h1 + "\n", "deno.zip"));
        Assert.Null(ToolManager.ParseHash($"{h1}  other.exe\n{h2}  other2.exe", "yt-dlp.exe"));
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

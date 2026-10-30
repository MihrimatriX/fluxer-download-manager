using System.Text.Json;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Xunit;

namespace Fluxer.Core.Tests;

/// Randomised workers against the planner: whatever order connections take, finish, fail and come
/// back in, every byte of the file is owned by exactly one segment and none is fetched twice.
public sealed class SegmentPlannerPropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    public void RandomSchedule_CoversFileExactlyOnce(int seed)
    {
        var rng = new Random(seed);
        const long size = 64L * 1024 * 1024, minSplit = 256 * 1024;
        var segments = new List<Segment> { new(0, 0, size) };
        var fetched = new List<(long From, long To)>();
        var workers = new List<Segment>();

        for (var step = 0; step < 20_000; step++)
        {
            // Open connections up to a random cap.
            while (workers.Count < rng.Next(1, 12) && SegmentPlanner.Take(segments, allowSplit: true, minSplit) is { } s) workers.Add(s);
            if (workers.Count == 0) break;

            var w = workers[rng.Next(workers.Count)];
            switch (rng.Next(10))
            {
                case 0: // connection fails: segment goes back to the pool, received bytes stay
                    w.Active = false;
                    workers.Remove(w);
                    break;
                default: // receive a chunk
                    var n = Math.Min(w.Remaining, rng.NextInt64(1, 512 * 1024));
                    fetched.Add((w.Position, w.Position + n));
                    w.Position += n;
                    w.Written = w.Position;
                    if (w.IsDone) { w.Active = false; workers.Remove(w); }
                    break;
            }
        }

        Assert.All(segments, s => Assert.True(s.IsDone, $"segment {s.Index} unfinished"));
        var ordered = segments.OrderBy(s => s.Start).ToList();
        Assert.Equal(0, ordered[0].Start);
        Assert.Equal(size, ordered[^1].End);
        for (var i = 1; i < ordered.Count; i++) Assert.Equal(ordered[i - 1].End, ordered[i].Start);

        // No byte was received twice and every byte was received once.
        var sorted = fetched.OrderBy(f => f.From).ToList();
        long cursor = 0;
        foreach (var (from, to) in sorted)
        {
            Assert.Equal(cursor, from);
            cursor = to;
        }
        Assert.Equal(size, cursor);
    }
}

public sealed class LinkSnifferTests
{
    private static readonly AppSettings Settings = new();

    [Fact]
    public void Picks_FileLinksAndMagnets_SkipsPages()
    {
        var text = """
            see https://example.com/blog/post and
            https://cdn.example.com/files/setup-1.2.exe, also (https://example.com/a%20b.mp4)
            magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=x
            https://example.com/ https://example.com/data.torrent
            """;
        var links = LinkSniffer.DownloadLinks(text, Settings);
        Assert.Equal(
        [
            "https://cdn.example.com/files/setup-1.2.exe",
            "https://example.com/a%20b.mp4",
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=x",
            "https://example.com/data.torrent",
        ], links);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just words")]
    [InlineData("magnet:?dn=no-hash")]
    [InlineData("ftp://example.com/file.zip")]
    public void Ignores_TextWithoutDownloadLinks(string? text) => Assert.Empty(LinkSniffer.DownloadLinks(text, Settings));

    [Fact]
    public void Dedupes() =>
        Assert.Single(LinkSniffer.DownloadLinks("https://x.org/a.zip https://x.org/a.zip", Settings));
}

public sealed class SettingsCompatibilityTests
{
    /// Settings saved by 0.8 have no AutoUpdate key and UpdateFeed = null: they must get updates now.
    [Fact]
    public void OldSettings_GetDefaultUpdates()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("""{"UpdateFeed":null,"HttpSlots":3}""")!;
        Assert.True(s.AutoUpdate);
        Assert.False(s.BetaUpdates);
        Assert.True(s.ScanDownloads);
        Assert.False(s.WatchClipboard);
        Assert.Null(s.WatchFolder);
        Assert.Null(s.UpdateFeed); // null = AppSettings.DefaultUpdateFeed
    }
}

using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Xunit;

namespace Fluxer.Integration.Tests;

/// Several addresses of one file: two FakeServers serving the same bytes act as primary and mirror.
public sealed class MirrorTests
{
    private static async Task<(FakeServer Primary, FakeServer Mirror)> StartPairAsync(long size = 8 * 1024 * 1024, long rate = 1024 * 1024)
    {
        var primary = await FakeServer.StartAsync();
        var mirror = await FakeServer.StartAsync();
        foreach (var s in (FakeServer[])[primary, mirror])
        {
            s.Size = size;
            s.RateFor = _ => rate; // per connection, so the download lasts long enough to spread out
        }
        return (primary, mirror);
    }

    private static SourceHealth MirrorHealth(HttpDownload d) => d.GetSources().Single(s => !s.Primary).Health;

    [Fact]
    public async Task Mirror_SharesTheWork_ProducesExactFile()
    {
        var (primary, mirror) = await StartPairAsync();
        await using var _ = primary;
        await using var __ = mirror;
        using var h = new Harness();
        var d = h.Create(primary.Url(), connections: 4, mirrors: [mirror.Url()]);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.True(mirror.Requests > 1, $"mirror requests {mirror.Requests}"); // more than the probe
        Assert.True(primary.Requests > 1, $"primary requests {primary.Requests}");
        Assert.Equal(SourceHealth.Healthy, MirrorHealth(d));
        Assert.Equal(primary.Url(), d.Record.Http!.Url);
    }

    [Fact]
    public async Task MirrorWithDifferentSize_IsRejected()
    {
        var (primary, mirror) = await StartPairAsync(rate: 4 * 1024 * 1024);
        await using var _ = primary;
        await using var __ = mirror;
        mirror.Size += 1;
        using var h = new Harness();
        var d = h.Create(primary.Url(), connections: 4, mirrors: [mirror.Url()]);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.Equal(1, mirror.Requests); // the probe only
        Assert.Equal(SourceHealth.Rejected, MirrorHealth(d));
    }

    [Fact]
    public async Task MirrorWithDifferentContent_IsRejected()
    {
        var (primary, mirror) = await StartPairAsync(rate: 4 * 1024 * 1024);
        await using var _ = primary;
        await using var __ = mirror;
        mirror.Version = 2; // same size, other ETag
        using var h = new Harness();
        var d = h.Create(primary.Url(), connections: 4, mirrors: [mirror.Url()]);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.Equal(SourceHealth.Rejected, MirrorHealth(d));
    }

    [Fact]
    public async Task FailingMirror_IsRetired_DownloadCompletes()
    {
        var (primary, mirror) = await StartPairAsync();
        await using var _ = primary;
        await using var __ = mirror;
        mirror.FailAfterRequests = 1; // answers the probe, then only 500s
        mirror.FailStatus = 500;
        using var h = new Harness();
        var d = h.Create(primary.Url(), connections: 4, mirrors: [mirror.Url()]);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.Equal(SourceHealth.Retired, MirrorHealth(d));
        Assert.True(mirror.Requests >= 4, $"mirror requests {mirror.Requests}"); // probe + 3 strikes
    }

    [Fact]
    public async Task MirrorLosingTheFile_IsRetiredAtOnce()
    {
        var (primary, mirror) = await StartPairAsync();
        await using var _ = primary;
        await using var __ = mirror;
        mirror.FailAfterRequests = 1;
        mirror.FailStatus = 404;
        using var h = new Harness();
        var d = h.Create(primary.Url(), connections: 4, mirrors: [mirror.Url()]);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.Equal(SourceHealth.Retired, MirrorHealth(d));
    }

    [Fact]
    public async Task ExpiredPrimaryLink_MirrorCarriesOn()
    {
        var (primary, mirror) = await StartPairAsync();
        await using var _ = primary;
        await using var __ = mirror;
        primary.ValidTokens = ["a"];
        using var h = new Harness();
        var d = h.Create(primary.Url("a"), connections: 4, mirrors: [mirror.Url()]);

        var run = Harness.RunAsync(d);
        await Task.Delay(1000);
        primary.ValidTokens = ["b"]; // "a" expires mid-download
        await run;

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.Equal(SourceHealth.Retired, d.GetSources()[0].Health);
        Assert.Equal(primary.Url("a"), d.Record.Http!.Url); // still the link a refresh would replace
    }

    [Fact]
    public async Task ExpiredPrimaryLink_AndDeadMirror_NeedsLinkRefresh()
    {
        var (primary, mirror) = await StartPairAsync();
        await using var _ = primary;
        await using var __ = mirror;
        primary.ValidTokens = ["a"];
        mirror.FailAfterRequests = 1;
        mirror.FailStatus = 410;
        using var h = new Harness();
        var d = h.Create(primary.Url("a"), connections: 4, mirrors: [mirror.Url()]);

        var run = Harness.RunAsync(d);
        await Task.Delay(1000);
        primary.ValidTokens = ["b"];
        await run;

        Assert.Equal(DownloadState.NeedsLinkRefresh, d.Record.State);
        Assert.True(d.Record.DoneBytes > 0);
    }

    [Fact]
    public async Task NoRangeSupport_IgnoresMirrors()
    {
        var (primary, mirror) = await StartPairAsync(size: 1024 * 1024, rate: 0);
        await using var _ = primary;
        await using var __ = mirror;
        primary.Ranges = false;
        using var h = new Harness();
        var d = h.Create(primary.Url(), connections: 4, mirrors: [mirror.Url()]);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.Equal(0, mirror.Requests);
        Assert.Equal(SourceHealth.Rejected, MirrorHealth(d));
    }

    [Fact]
    public async Task Resume_UsesMirrors_ProducesExactFile()
    {
        var (primary, mirror) = await StartPairAsync(size: 16 * 1024 * 1024);
        await using var _ = primary;
        await using var __ = mirror;
        using var h = new Harness();
        var d = h.Create(primary.Url(), connections: 4, mirrors: [mirror.Url()]);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.2)))
            await d.RunAsync(cts.Token);
        Assert.Equal(DownloadState.Paused, d.Record.State);

        var back = h.Db.LoadAll().Single();
        Assert.Equal([mirror.Url()], back.Http!.Mirrors);
        primary.RateFor = mirror.RateFor = _ => 0;
        var resumed = h.Create(primary.Url(), existing: back);
        await Harness.RunAsync(resumed);

        Assert.Equal(DownloadState.Completed, resumed.Record.State);
        Assert.Equal(primary.ExpectedSha256(), Harness.Sha(resumed.Record.FullPath));
    }

    [Theory]
    [InlineData("https://mirror.example.com/f.iso", true)]
    [InlineData("http://mirror.example.com/f.iso", true)]
    [InlineData("ftp://mirror.example.com/f.iso", false)]
    [InlineData("mirror.example.com/f.iso", false)]
    [InlineData("magnet:?xt=urn:btih:abc", false)]
    public void MirrorUrl_MustBeHttp(string url, bool ok) => Assert.Equal(ok, HttpInfo.IsMirrorUrl(url));

    [Fact]
    public void CleanMirrors_DropsInvalidDuplicatesAndThePrimary()
    {
        var list = HttpInfo.CleanMirrors([" https://b/f ", "https://b/f", "https://a/f", "ftp://c/f", "", "https://d/f"], "https://a/f");
        Assert.Equal(["https://b/f", "https://d/f"], list);
    }
}

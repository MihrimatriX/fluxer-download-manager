using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Xunit;

namespace Fluxer.Integration.Tests;

public sealed class ProbeTests
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    private const string Page = "<!DOCTYPE html><html><body>Just a moment...</body></html>";

    [Fact]
    public async Task Probe_TellsFilesPagesAndTorrentsApart()
    {
        await using var server = await FakeServer.StartAsync();
        server.Texts["/challenge"] = Page;
        server.Texts["/t"] = "d8:announce35:udp://tracker.example:1337/announce4:infod6:lengthi1ee";
        using var http = new HttpEngine();

        var file = await HttpProbe.RunAsync(http, server.Url(), null, CancellationToken.None);
        Assert.Equal(ProbeKind.File, file.Kind);
        Assert.Equal(server.Size, file.Size);
        Assert.True(file.Resumable);
        Assert.Equal("file.bin", file.FileName);

        Assert.Equal(ProbeKind.Html, (await HttpProbe.RunAsync(http, server.BaseUrl + "/challenge", null, CancellationToken.None)).Kind);
        Assert.Equal(ProbeKind.Torrent, (await HttpProbe.RunAsync(http, server.BaseUrl + "/t", null, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task Probe_FallsBackToOwnUserAgent_WhenTheBrowserOneIsRefused()
    {
        await using var server = await FakeServer.StartAsync();
        server.RejectUserAgentContaining = "Chrome";
        using var http = new HttpEngine();

        var (result, ctx) = await HttpProbe.RunWithFallbackAsync(http, server.Url(), new RequestContext("https://ref/", Chrome, "a=b"), CancellationToken.None);

        Assert.Equal(ProbeKind.File, result.Kind);
        Assert.NotNull(ctx);
        Assert.Null(ctx.UserAgent);            // ours is used from now on
        Assert.Equal("a=b", ctx.Cookies);      // the rest of the browser context is kept
    }

    [Fact]
    public async Task Download_ThatGetsAWebPage_FailsInsteadOfSavingHtml()
    {
        await using var server = await FakeServer.StartAsync();
        server.Texts["/setup.zip"] = Page;
        using var h = new Harness();
        var d = h.Create(server.BaseUrl + "/setup.zip");

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Failed, d.Record.State);
        Assert.Equal("got-html", d.Record.Error);
        Assert.Empty(Directory.GetFiles(h.Dir, "*.html"));
    }
}

public sealed class QueueOrderTests
{
    [Fact]
    public void Reorder_MovesSelectionAsABlock_AndPersists()
    {
        using var h = new Harness();
        var queue = new DownloadQueue(h.Db, () => h.Settings, r => h.Create(r.Http!.Url, existing: r));
        var ids = Enumerable.Range(0, 5)
            .Select(i => queue.Add(new DownloadRecord { Kind = DownloadKind.Http, SaveDirectory = h.Dir, Http = new HttpInfo { Url = $"http://x/{i}" } }, start: false).Id)
            .ToList();
        string Order() => string.Join(",", h.Db.LoadAll().OrderBy(r => r.QueueOrder).Select(r => r.Http!.Url[^1]));

        queue.Reorder([ids[3], ids[4]], DownloadQueue.Move.Top);
        Assert.Equal("3,4,0,1,2", Order());
        queue.Reorder([ids[0]], DownloadQueue.Move.Up);
        Assert.Equal("3,0,4,1,2", Order());
        queue.Reorder([ids[3]], DownloadQueue.Move.Bottom);
        Assert.Equal("0,4,1,2,3", Order());
        queue.Reorder([ids[4]], DownloadQueue.Move.Down);
        Assert.Equal("0,1,4,2,3", Order());
    }
}

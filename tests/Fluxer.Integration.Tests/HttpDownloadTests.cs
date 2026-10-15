using System.Diagnostics;
using System.Security.Cryptography;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Platform.Windows;
using Fluxer.Core.Storage;
using Xunit;

namespace Fluxer.Integration.Tests;

public sealed class Harness : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("fluxer-test-").FullName;
    public Database Db { get; }
    public HttpEngine Engine { get; } = new();
    public AppSettings Settings { get; } = new() { UseCategoryFolders = false };

    public Harness()
    {
        Db = new Database(Path.Combine(Dir, "test.db"));
        Settings.DownloadDirectory = Dir;
    }

    public HttpDownload Create(string url, int connections = 4, DownloadRecord? existing = null)
    {
        var rec = existing;
        if (rec is null)
        {
            rec = new DownloadRecord { Kind = DownloadKind.Http, SaveDirectory = Dir, Http = new HttpInfo { Url = url, MaxConnections = connections } };
            Db.Insert(rec);
        }
        return new HttpDownload(rec, Engine, Db, () => Settings)
        {
            MinSplit = 128 * 1024,
            BackoffUnit = TimeSpan.FromMilliseconds(50),
            StallTimeout = TimeSpan.FromSeconds(5),
            CheckpointInterval = TimeSpan.FromMilliseconds(200),
            RampStep = TimeSpan.FromMilliseconds(20),
        };
    }

    public static async Task RunAsync(IDownload d, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        await d.RunAsync(cts.Token);
    }

    public static byte[] Sha(string path)
    {
        using var fs = File.OpenRead(path);
        return SHA256.HashData(fs);
    }

    public void Dispose()
    {
        Engine.Dispose();
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class HttpDownloadTests
{
    [Fact]
    public async Task MultiConnection_RangeDownload_ProducesExactFile()
    {
        await using var server = await FakeServer.StartAsync();
        server.RateFor = _ => 2 * 1024 * 1024;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 6);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
        Assert.True(server.PeakConcurrent > 1, $"peak concurrency {server.PeakConcurrent}");
        Assert.False(File.Exists(d.Record.FullPath + ".part"));
        Assert.True(File.Exists(d.Record.FullPath + ":Zone.Identifier"), "Mark of the Web missing");
    }

    [Fact]
    public async Task NoRangeSupport_FallsBackToSingleConnection()
    {
        await using var server = await FakeServer.StartAsync();
        server.Ranges = false;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 8);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.False(d.Record.Http!.Resumable);
        Assert.Equal(1, server.PeakConcurrent);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
    }

    [Fact]
    public async Task DroppedConnections_AreRetried()
    {
        await using var server = await FakeServer.StartAsync();
        server.DropAfter = 300 * 1024;
        server.DropResponses = 5;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 4);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
    }

    [Fact]
    public async Task SlowSegment_IsStolenByFasterConnections()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 4 * 1024 * 1024;
        // Anything starting at byte 0 crawls at 64 KiB/s (alone it would need ~64 s).
        server.RateFor = from => from == 0 ? 64 * 1024 : 0;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 4);

        var sw = Stopwatch.StartNew();
        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
    }

    [Fact]
    public async Task TooManyRequests_ReducesConnections_AndCompletes()
    {
        await using var server = await FakeServer.StartAsync();
        server.MaxConcurrent = 2;
        server.RateFor = _ => 4 * 1024 * 1024;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 8);

        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.True(server.Throttled > 0);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
    }

    [Fact]
    public async Task PauseThenResume_FromCheckpoint_ProducesExactFile()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 16 * 1024 * 1024;
        server.RateFor = _ => 1024 * 1024;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 4);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.2)))
            await d.RunAsync(cts.Token);
        Assert.Equal(DownloadState.Paused, d.Record.State);
        Assert.True(d.Record.DoneBytes > 0);
        var requestsBefore = server.Requests;

        server.RateFor = _ => 0;
        var resumed = h.Create(server.Url(), existing: h.Db.LoadAll().Single());  // as after a restart
        await Harness.RunAsync(resumed);

        Assert.Equal(DownloadState.Completed, resumed.Record.State);
        Assert.True(server.Requests > requestsBefore);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(resumed.Record.FullPath));
    }

    [Fact]
    public async Task ETagChanged_OnResume_FailsWithFileChanged()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 16 * 1024 * 1024;
        server.RateFor = _ => 1024 * 1024;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 2);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            await d.RunAsync(cts.Token);

        server.Version = 2; // new content, new ETag
        var resumed = h.Create(server.Url(), existing: h.Db.LoadAll().Single());
        await Harness.RunAsync(resumed);

        Assert.Equal(DownloadState.Failed, resumed.Record.State);
        Assert.Equal("file-changed", resumed.Record.Error);
    }

    [Fact]
    public async Task ExpiredLink_NeedsRefresh_ThenManualUrlResumes()
    {
        await using var server = await FakeServer.StartAsync();
        server.ValidTokens = ["a"];
        server.Size = 16 * 1024 * 1024;
        server.RateFor = _ => 1024 * 1024;
        using var h = new Harness();
        var d = h.Create(server.Url("a"), connections: 4);

        var run = Harness.RunAsync(d);
        await Task.Delay(1000);
        server.ValidTokens = ["b"]; // "a" expires mid-download
        await run;
        Assert.Equal(DownloadState.NeedsLinkRefresh, d.Record.State);

        // A link to a different-size file is rejected.
        await using (var other = await FakeServer.StartAsync())
        {
            other.Size = 1234;
            var bad = await d.CheckNewLinkAsync(other.Url(), null, CancellationToken.None);
            Assert.True(bad.Reachable);
            Assert.False(bad.SizeMatches);
        }

        var check = await d.CheckNewLinkAsync(server.Url("b"), null, CancellationToken.None);
        Assert.True(check.SizeMatches);
        Assert.False(check.ValidatorChanged);
        server.RateFor = _ => 0;
        d.ApplyNewLink(server.Url("b"), null, check);
        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
    }

    [Fact]
    public async Task MultipleConnections_AreMeasurablyFaster_WhenServerLimitsPerConnection()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 3 * 1024 * 1024;
        server.RateFor = _ => 512 * 1024; // per connection
        using var h = new Harness();

        var sw = Stopwatch.StartNew();
        var single = h.Create(server.Url(), connections: 1);
        await Harness.RunAsync(single);
        var singleTime = sw.Elapsed;

        sw.Restart();
        var multi = h.Create(server.Url(), connections: 8);
        await Harness.RunAsync(multi);
        var multiTime = sw.Elapsed;

        Assert.Equal(DownloadState.Completed, multi.Record.State);
        Assert.True(multiTime * 2.5 < singleTime, $"single {singleTime}, multi {multiTime}");
    }

    [Fact]
    public async Task UnknownLength_ChunkedResponse_Completes()
    {
        await using var server = await FakeServer.StartAsync();
        server.Ranges = false;
        server.Chunked = true;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 4);
        await Harness.RunAsync(d);
        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.False(d.Record.Http!.Resumable);
        Assert.Equal(server.Size, d.Record.TotalBytes);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
    }

    [Fact]
    public async Task HostnameWithUnreachableAddressFamily_FallsBackToTheOther()
    {
        // FakeServer listens on 127.0.0.1 only; "localhost" also resolves to ::1, which must not stall us.
        await using var server = await FakeServer.StartAsync();
        server.Size = 512 * 1024;
        using var h = new Harness();
        var d = h.Create(server.Url().Replace("127.0.0.1", "localhost", StringComparison.Ordinal), connections: 4);

        var sw = Stopwatch.StartNew();
        await Harness.RunAsync(d);

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task ManyConnectionsThrottledToOne_NeverLosesTheLastWorker()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 8 * 1024 * 1024;
        server.MaxConcurrent = 1;
        server.RateFor = _ => 8 * 1024 * 1024;
        using var h = new Harness();
        var d = h.Create(server.Url(), connections: 16);

        await Harness.RunAsync(d, TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, d.Record.State);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(d.Record.FullPath));
    }

    [Fact]
    public void SparsePreallocation_FirstWriteAtEnd_DoesNotBlock()
    {
        using var h = new Harness();
        var path = Path.Combine(h.Dir, "big.part");
        const long size = 4L * 1024 * 1024 * 1024; // 4 GiB, costs nothing when sparse
        using (var handle = SparseFile.OpenPreallocated(path, size))
        {
            var sw = Stopwatch.StartNew();
            RandomAccess.Write(handle, new byte[1 << 20], size - (1 << 20));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 500, $"first write at end took {sw.ElapsedMilliseconds} ms");
        }
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
    }
}

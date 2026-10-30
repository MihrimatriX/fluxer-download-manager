using System.Diagnostics;
using Fluxer.Core;
using Fluxer.Core.Downloads;
using Xunit;

namespace Fluxer.Integration.Tests;

public sealed class QueueAndCrashTests
{
    [Fact]
    public async Task Queue_TenDownloads_ThreeAtATime_PauseResumeDelete()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 1024 * 1024;
        server.RateFor = _ => 1024 * 1024;
        using var h = new Harness();
        await using var engine = new FluxerEngine(Path.Combine(h.Dir, "data"));
        var s = engine.Settings;
        s.DownloadDirectory = Path.Combine(h.Dir, "out");
        s.UseCategoryFolders = false;
        s.ConnectionsPerDownload = 2;
        await engine.SaveSettingsAsync(s);

        var running = 0;
        var peak = 0;
        var finished = new TaskCompletionSource();
        var gate = new Lock();
        engine.Queue.StateChanged += d =>
        {
            lock (gate)
            {
                running = engine.Queue.Items.Count(x => x.State is DownloadState.Starting or DownloadState.Downloading);
                peak = Math.Max(peak, running);
                if (engine.Queue.Items.All(x => x.State is DownloadState.Completed or DownloadState.Paused))
                    finished.TrySetResult();
            }
        };

        var items = Enumerable.Range(0, 10).Select(i => engine.AddHttp($"{server.Url()}?n={i}", $"f{i}.bin")).ToList();
        Assert.Equal(10, engine.Queue.Items.Count);

        await Task.Delay(300);
        await engine.Queue.PauseAsync(items[9].Id);   // still queued → paused without running
        await engine.Queue.RemoveAsync(items[8].Id, deleteFiles: true);
        Assert.Equal(DownloadState.Paused, items[9].State);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(peak <= 3, $"peak running {peak}");
        Assert.Equal(9, engine.Queue.Items.Count);

        engine.Queue.Resume(items[9].Id);
        var last = new TaskCompletionSource();
        engine.Queue.StateChanged += d => { if (d.Id == items[9].Id && d.State == DownloadState.Completed) last.TrySetResult(); };
        await last.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var expected = server.ExpectedSha256();
        foreach (var d in engine.Queue.Items)
        {
            Assert.Equal(DownloadState.Completed, d.State);
            Assert.Equal(expected, Harness.Sha(d.Record.FullPath));
        }
        Assert.False(File.Exists(Path.Combine(s.DownloadDirectory, "f8.bin")));
    }

    [Fact]
    public async Task KilledProcess_ResumesWhereItLeftOff_WithCorrectHash()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 16 * 1024 * 1024;
        server.RateFor = _ => 1024 * 1024; // 4 conns → ~4 s total
        using var h = new Harness();
        var data = Path.Combine(h.Dir, "data");
        var cli = Path.Combine(AppContext.BaseDirectory, "fluxer-cli.dll");

        using (var p = StartCli(cli, server.Url(), h.Dir, data))
        {
            // CLI startup time varies (JIT, disk, antivirus), so time the kill from the first request, not from launch.
            var wait = Stopwatch.StartNew();
            while (server.Requests == 0 && wait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            await Task.Delay(1500);
            p.Kill(entireProcessTree: true); // no graceful shutdown, no final checkpoint
            await p.WaitForExitAsync();
        }
        Assert.True(File.Exists(Path.Combine(h.Dir, "file.bin.part")));
        var requestsAfterKill = server.Requests;

        server.RateFor = _ => 0;
        using (var p = StartCli(cli, server.Url(), h.Dir, data))
        {
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, output + await p.StandardError.ReadToEndAsync());
            Assert.Contains("resuming", output);
        }

        Assert.True(server.Requests > requestsAfterKill);
        Assert.Equal(server.ExpectedSha256(), Harness.Sha(Path.Combine(h.Dir, "file.bin")));
    }

    private static Process StartCli(string cli, string url, string outDir, string data)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { cli, "get", url, "--out", outDir, "--data", data, "-c", "4" }) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }
}

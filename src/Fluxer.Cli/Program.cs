using System.Diagnostics;
using System.Globalization;
using Fluxer.Core;
using Fluxer.Core.Downloads;
using Fluxer.Core.Platform.Windows;
using Serilog;

// Developer tool for the engine (not shipped).
//   fluxer-cli get <url> [--out DIR] [--data DIR] [-c N] [--limit BYTES_PER_SEC] [--mirror URL]...
//       Downloads (or resumes, if the same URL is unfinished in --data) and exits 0 on success.
//       Each --mirror adds another address of the same file.
//   fluxer-cli sparse <file> <GiB>
//       Measures the first write at the end of a freshly preallocated sparse file.

if (args.Length >= 3 && args[0] == "sparse") return Sparse(args[1], double.Parse(args[2], CultureInfo.InvariantCulture));
if (args.Length < 2 || args[0] != "get")
{
    Console.Error.WriteLine("usage: fluxer-cli get <url> [--out DIR] [--data DIR] [-c N] [--limit B/s] [--mirror URL]... | sparse <file> <GiB>");
    return 2;
}

var url = args[1];
string Opt(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
var data = Path.GetFullPath(Opt("--data", "fluxer-cli-data"));
var output = Path.GetFullPath(Opt("--out", "."));
var mirrors = args.Select((a, i) => (a, i)).Where(x => x.a == "--mirror" && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();
if (mirrors.FirstOrDefault(m => !HttpInfo.IsMirrorUrl(m)) is { } badMirror)
{
    Console.Error.WriteLine($"not an http(s) address: {badMirror}");
    return 2;
}

Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Debug()
    .WriteTo.File(Path.Combine(data, "cli.log")).CreateLogger();
await using var engine = new FluxerEngine(data);
var settings = engine.Settings;
settings.DownloadDirectory = output;
settings.UseCategoryFolders = false;
settings.ConnectionsPerDownload = int.Parse(Opt("-c", "8"), CultureInfo.InvariantCulture);
settings.HttpLimit = long.Parse(Opt("--limit", "0"), CultureInfo.InvariantCulture);
await engine.SaveSettingsAsync(settings);

var done = new TaskCompletionSource<DownloadState>(TaskCreationOptions.RunContinuationsAsynchronously);
engine.Queue.StateChanged += d =>
{
    if (d.State is DownloadState.Completed or DownloadState.Failed or DownloadState.NeedsLinkRefresh or DownloadState.DiskFull)
        done.TrySetResult(d.State);
};

engine.Queue.Load(); // resumes interrupted downloads
var download = engine.Queue.Items.FirstOrDefault(d => d.Record.Http?.Url == url && d.State != DownloadState.Completed);
if (download is null)
    download = engine.AddHttp(url, mirrors: mirrors);
else
    Console.WriteLine($"resuming #{download.Id} from {download.Record.DoneBytes:N0} bytes");

var sw = Stopwatch.StartNew();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
while (!done.Task.IsCompleted && !cts.IsCancellationRequested)
{
    await Task.WhenAny(done.Task, Task.Delay(500, cts.Token).ContinueWith(_ => { }));
    var p = download.GetProgress();
    Console.Write($"\r{download.State,-12} {p.BytesDone / 1048576.0,9:F1} / {(p.TotalBytes ?? 0) / 1048576.0,9:F1} MiB  {p.BytesPerSecond / 1048576.0,7:F2} MiB/s  conn {p.Connections,2}   ");
}
Console.WriteLine();
if (cts.IsCancellationRequested) { await engine.Queue.PauseAsync(download.Id); Console.WriteLine("paused"); return 1; }

var state = await done.Task;
Console.WriteLine($"{state} in {sw.Elapsed.TotalSeconds:F2}s → {download.Record.FullPath} {download.Record.Error}");
return state == DownloadState.Completed ? 0 : 1;

static int Sparse(string path, double gib)
{
    var size = (long)(gib * 1024 * 1024 * 1024);
    File.Delete(path);
    var sw = Stopwatch.StartNew();
    using (var h = SparseFile.OpenPreallocated(path, size))
    {
        var prealloc = sw.Elapsed;
        sw.Restart();
        RandomAccess.Write(h, new byte[1 << 20], size - (1 << 20));
        Console.WriteLine($"preallocate {prealloc.TotalMilliseconds:F1} ms, first write at end {sw.Elapsed.TotalMilliseconds:F1} ms, sparse={File.GetAttributes(path).HasFlag(FileAttributes.SparseFile)}");
    }
    File.Delete(path);
    return 0;
}

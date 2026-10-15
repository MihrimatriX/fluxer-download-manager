using System.Diagnostics;
using System.Globalization;
using Fluxer.Core.Downloads;
using Fluxer.Core.Storage;
using Serilog;

namespace Fluxer.Core.Media;

/// yt-dlp extracts and downloads (with --concurrent-fragments), ffmpeg merges; we manage the
/// process and parse its progress. Pause = kill, resume = same command (yt-dlp continues .part).
public sealed class MediaDownload : IDownload
{
    private const string Marker = "FLX|";
    private const string ProgressTemplate =
        "download:" + Marker + "%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|" +
        "%(progress.speed)s|%(progress.eta)s|%(progress.fragment_index)s|%(progress.fragment_count)s";

    private readonly ToolManager _tools;
    private readonly Database _db;
    private readonly Func<AppSettings> _settings;
    private readonly Func<int> _activeMedia;
    private long _done, _total, _speed, _eta;
    private int _fragment, _fragments;

    public MediaDownload(DownloadRecord record, ToolManager tools, Database db, Func<AppSettings> settings, Func<int> activeMedia)
    {
        Record = record;
        _tools = tools;
        _db = db;
        _settings = settings;
        _activeMedia = activeMedia;
    }

    public DownloadRecord Record { get; }
    public long Id => Record.Id;
    public DownloadKind Kind => Record.Kind;
    public DownloadState State => Record.State;
    public string FileName => Record.FileName;
    private MediaInfo Info => Record.Media!;
    public (int Index, int Count) Fragments => (_fragment, _fragments);

    public event Action<IDownload>? StateChanged;

    public DownloadProgress GetProgress()
    {
        // Live numbers describe the current stream (video, then audio); only meaningful while running.
        if (Record.State != DownloadState.Downloading)
            return new(Record.DoneBytes, Record.TotalBytes, 0, null, 0);
        var total = Interlocked.Read(ref _total);
        var eta = Interlocked.Read(ref _eta);
        return new(Interlocked.Read(ref _done), total > 0 ? total : Record.TotalBytes, Interlocked.Read(ref _speed),
            eta > 0 ? TimeSpan.FromSeconds(eta) : null, _fragments > 0 ? _fragment : 0);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        SetState(DownloadState.Starting, null);
        if (!_tools.IsInstalled) { SetState(DownloadState.Failed, "media-tools-missing"); return; }

        string? cookieFile = null;
        var errors = new Queue<string>();
        try
        {
            cookieFile = Info.Cookies is { Length: > 0 } c ? Cookies.WritePrivateFile(TempDirectory(_tools), c) : null;
            var psi = YtDlp.BaseStartInfo(_tools, cookieFile);
            Directory.CreateDirectory(Record.SaveDirectory);
            var stem = Record.FileName; // sanitized title; gets its extension only on completion
            var args = new List<string>
            {
                "--newline", "--progress", "--no-playlist", "--windows-filenames", "--progress-template", ProgressTemplate,
                "--concurrent-fragments", "4", "-f", Info.FormatSpec, "--merge-output-format", "mp4/mkv",
                "-o", Path.Combine(Record.SaveDirectory, stem + ".%(ext)s"),
                "--print", "after_move:filepath",
            };
            if (Info.AudioOnly) args.Add("-x");
            var limit = RateLimit();
            if (limit > 0) { args.Add("--limit-rate"); args.Add(limit.ToString(CultureInfo.InvariantCulture)); }
            args.Add(Info.PageUrl);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            Platform.Windows.ChildProcessJob.Assign(p);
            SetState(DownloadState.Downloading, null);
            string? finalPath = null;

            var stderr = Task.Run(async () =>
            {
                while (await p.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    Log.Debug("yt-dlp[{Id}] {Line}", Record.Id, line);
                    lock (errors) { errors.Enqueue(line); if (errors.Count > 50) errors.Dequeue(); }
                }
            }, CancellationToken.None);

            await using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
            {
                while (await p.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    if (line.StartsWith(Marker, StringComparison.Ordinal)) ParseProgress(line[Marker.Length..]);
                    else if (Path.IsPathFullyQualified(line.Trim())) finalPath = line.Trim();
                    else Log.Debug("yt-dlp[{Id}] {Line}", Record.Id, line);
                }
                await p.WaitForExitAsync(CancellationToken.None);
                await stderr;
            }

            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0)
            {
                string err;
                lock (errors) err = string.Join('\n', errors);
                throw new MediaException(MediaErrors.Classify(err), err);
            }

            if (finalPath is not null && File.Exists(finalPath))
            {
                Record.FileName = Path.GetFileName(finalPath);
                Record.TotalBytes = Record.DoneBytes = new FileInfo(finalPath).Length;
            }
            Record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SetState(DownloadState.Completed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetState(DownloadState.Paused, null);
        }
        catch (Exception e)
        {
            var code = e is MediaException me ? me.Code : e.Message;
            Log.Error("Media download {Id} failed: {Code} {Details}", Record.Id, code, (e as MediaException)?.Details ?? e.ToString());
            SetState(DownloadState.Failed, code);
            // An outdated yt-dlp is the most common cause; check for an update in the background.
            _ = Task.Run(async () =>
            {
                try { await _tools.UpdateYtDlpAsync(CancellationToken.None); }
                catch (Exception ue) { Log.Warning(ue, "yt-dlp update check failed"); }
            });
        }
        finally
        {
            if (cookieFile is not null) File.Delete(cookieFile);
        }
    }

    /// Global HTTP limit split evenly across active yt-dlp processes (approximate by design),
    /// capped by this download's own limit.
    private long RateLimit()
    {
        var global = _settings().HttpLimit;
        var share = global > 0 ? global / Math.Max(1, _activeMedia()) : 0;
        var own = Record.SpeedLimit;
        return share > 0 && own > 0 ? Math.Min(share, own) : Math.Max(share, own);
    }

    private void ParseProgress(string payload)
    {
        var f = payload.Split('|');
        if (f.Length < 7) return;
        var done = Num(f[0]);
        var total = Num(f[1]) ?? Num(f[2]);
        if (done is { } d) Interlocked.Exchange(ref _done, d);
        if (total is { } t) Interlocked.Exchange(ref _total, t);
        Interlocked.Exchange(ref _speed, Num(f[3]) ?? 0);
        Interlocked.Exchange(ref _eta, Num(f[4]) ?? 0);
        _fragment = (int)(Num(f[5]) ?? 0);
        _fragments = (int)(Num(f[6]) ?? 0);

        static long? Num(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : null;
    }

    /// Removes cookie files left behind by a crash.
    private static string TempDirectory(ToolManager tools) => Path.GetFullPath(Path.Combine(tools.Directory, "..", "tmp"));

    public static void CleanupTemp(ToolManager tools)
    {
        var dir = TempDirectory(tools);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "cookies-*.txt"))
            try { File.Delete(f); } catch (IOException) { }
    }

    public void DeleteFiles()
    {
        try
        {
            if (Record.State == DownloadState.Completed) File.Delete(Record.FullPath);
            var stem = Record.State == DownloadState.Completed ? Path.GetFileNameWithoutExtension(Record.FileName) : Record.FileName;
            if (Directory.Exists(Record.SaveDirectory))
                foreach (var f in Directory.EnumerateFiles(Record.SaveDirectory, stem + ".*"))
                    if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || f.Contains(".part-Frag", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                        File.Delete(f);
        }
        catch (IOException e) { Log.Warning(e, "Deleting media files {Id}", Record.Id); }
    }

    private void SetState(DownloadState state, string? error)
    {
        Record.State = state;
        Record.Error = error;
        if (state != DownloadState.Completed) Record.DoneBytes = Interlocked.Read(ref _done);
        _db.Update(Record);
        StateChanged?.Invoke(this);
    }
}

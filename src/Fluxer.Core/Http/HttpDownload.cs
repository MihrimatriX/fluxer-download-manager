using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Fluxer.Core.Downloads;
using Fluxer.Core.Platform.Windows;
using Fluxer.Core.Storage;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace Fluxer.Core.Http;

/// Multi-connection ranged HTTP download written straight into a sparse ".part" file.
/// Workers pull work from SegmentPlanner: when one finishes, it splits the largest remaining
/// segment, so fast connections keep stealing from slow ones until the file is done.
public sealed class HttpDownload : IDownload
{
    private const int BufferSize = 1 << 20;   // per connection, rented
    private const int ReadChunk = 64 * 1024;  // granularity of rate limiting
    private const int MaxRetries = 8;

    private readonly HttpEngine _engine;
    private readonly Database _db;
    private readonly Func<AppSettings> _settings;
    private readonly Lock _lock = new();
    private readonly List<Segment> _segments = [];
    private readonly TokenBucket _bucket = new();
    private readonly SpeedMeter _speed = new();
    private readonly SemaphoreSlim _reresolveGate = new(1, 1);
    private readonly Lock _checkpointLock = new();

    private SafeFileHandle? _file;
    private long _received;
    private int _activeWorkers;
    private int _connectionLimit;
    private CancellationTokenSource? _stop;
    private DownloadState? _stopState;
    private string? _stopError;
    private string? _lastError;
    private long _lastReresolve;
    private volatile bool _throttled;

    public HttpDownload(DownloadRecord record, HttpEngine engine, Database db, Func<AppSettings> settings)
    {
        Record = record;
        _engine = engine;
        _db = db;
        _settings = settings;
        _bucket.BytesPerSecond = record.SpeedLimit;
        _received = record.DoneBytes;
    }

    public DownloadRecord Record { get; }
    public long Id => Record.Id;
    public DownloadKind Kind => Record.Kind;
    public DownloadState State => Record.State;
    public string FileName => Record.FileName;
    private HttpInfo Http => Record.Http!;
    private string PartPath => Record.FullPath + ".part";

    internal long MinSplit { get; init; } = SegmentPlanner.DefaultMinSplit;
    internal TimeSpan BackoffUnit { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan RampStep { get; init; } = TimeSpan.FromMilliseconds(150);

    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; private set; } = [];

    public event Action<IDownload>? StateChanged;

    public long SpeedLimit
    {
        get => Record.SpeedLimit;
        set { Record.SpeedLimit = value; _bucket.BytesPerSecond = value; _db.Update(Record); }
    }

    public DownloadProgress GetProgress()
    {
        var done = Interlocked.Read(ref _received);
        var speed = Record.State == DownloadState.Downloading ? _speed.Sample(done) : 0;
        return new(done, Record.TotalBytes, speed, SpeedMeter.Eta(done, Record.TotalBytes, speed), Volatile.Read(ref _activeWorkers));
    }

    /// Received-position snapshot for the segment map.
    public SegmentRow[] GetSegments()
    {
        lock (_lock) return [.. _segments.Select(s => new SegmentRow(s.Index, s.Start, s.End, s.Position))];
    }

    /// Segments with a live connection on them (for the per-connection table).
    public HashSet<int> GetActiveSegments()
    {
        lock (_lock) return [.. _segments.Where(s => s.Active && !s.IsDone).Select(s => s.Index)];
    }

    // ================= run =================

    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop = stop;
        _stopState = null;
        _stopError = _lastError = null;
        _speed.Reset();
        SetState(DownloadState.Starting, null);

        Task? checkpoints = null;
        try
        {
            var first = await PrepareAsync(stop.Token);
            _connectionLimit = Http.Resumable ? Math.Clamp(Http.MaxConnections, 1, 32) : 1;
            SetState(DownloadState.Downloading, null);

            // Slow start: open connections one by one and stop at the first push-back, instead of a
            // burst that trips per-IP limits (some servers answer a burst with a temporary IP ban).
            _throttled = false;
            var workers = new List<Task>();
            if (first is { } f) workers.Add(WorkerAsync(f.Response, f.Segment, stop.Token));
            for (var i = workers.Count; i < _connectionLimit; i++)
            {
                workers.Add(DelayedWorkerAsync(RampStep * i, stop.Token));
            }
            checkpoints = CheckpointLoopAsync(stop.Token);
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (StopException e) { RequestStop(e.State, e.Message); }
        catch (Exception e) when (DiskErrors.IsDiskFull(e)) { RequestStop(DownloadState.DiskFull, "disk-full"); }
        catch (Exception e)
        {
            Log.Error(e, "Download {Id} failed", Record.Id);
            RequestStop(DownloadState.Failed, e.Message);
        }
        finally
        {
            await stop.CancelAsync();
            if (checkpoints is not null) await checkpoints;
        }

        await FinishAsync(ct.IsCancellationRequested);
    }

    private async Task<(HttpResponseMessage Response, Segment Segment)?> PrepareAsync(CancellationToken ct)
    {
        if (Http.Resumable && Record.TotalBytes is { } total && File.Exists(PartPath))
        {
            var rows = _db.LoadSegments(Record.Id);
            if (rows.Count > 0)
            {
                lock (_lock)
                {
                    _segments.Clear();
                    _segments.AddRange(rows.Select(Segment.FromRow));
                    _received = rows.Sum(r => r.Position - r.Start);
                }
                _file = SparseFile.OpenPreallocated(PartPath, total);
                return null;
            }
        }

        // Fresh start (new, non-resumable restart, or lost .part).
        var resp = await ProbeAsync(ct);
        try
        {
            Http.FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? Http.Url;
            // A login/captcha/Cloudflare page instead of the file: fail clearly rather than save it as "x.html".
            if (FileNameResolver.IsHtml(resp.Content.Headers.ContentType?.MediaType)
                && (Http.RejectHtml || FileNameResolver.NamesAFile(new Uri(Http.Url))))
                throw new StopException(DownloadState.Failed, "got-html");
            ResponseHeaders = [.. resp.Headers.Concat(resp.Content.Headers).Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value)))];

            long? size;
            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                size = resp.Content.Headers.ContentRange?.Length;
                Http.Resumable = size is not null && resp.Content.Headers.ContentRange?.From == 0;
            }
            else
            {
                size = resp.Content.Headers.ContentLength;
                Http.Resumable = false;
            }
            Record.TotalBytes = size;
            Http.ETag = resp.Headers.ETag is { IsWeak: false } etag ? etag.Tag : null;
            Http.LastModified = resp.Content.Headers.LastModified;

            var settings = _settings();
            if (File.Exists(PartPath))
                File.Delete(PartPath);       // restart of a non-resumable download keeps its name
            else
                ChooseFileName(resp, settings);

            Directory.CreateDirectory(Record.SaveDirectory);
            if (size is { } s && s > DiskErrors.FreeBytes(Record.SaveDirectory))
                throw new StopException(DownloadState.DiskFull, "disk-full");

            _file = size is { } len ? SparseFile.OpenPreallocated(PartPath, len) : SparseFile.OpenAppend(PartPath);
            var seg = new Segment(0, 0, size ?? long.MaxValue) { Active = true };
            lock (_lock)
            {
                _segments.Clear();
                _segments.Add(seg);
                _received = 0;
            }
            _db.Update(Record);
            Checkpoint();
            return (resp, seg);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    private void ChooseFileName(HttpResponseMessage resp, AppSettings settings)
    {
        var name = string.IsNullOrWhiteSpace(Record.FileName)
            ? FileNameResolver.Resolve(resp.Content.Headers.ContentDisposition, resp.RequestMessage!.RequestUri!, resp.Content.Headers.ContentType?.MediaType)
            : FileNameSanitizer.Sanitize(Record.FileName);

        if (string.IsNullOrWhiteSpace(Record.SaveDirectory))
        {
            Record.Category = settings.CategoryFor(name);
            Record.SaveDirectory = settings.UseCategoryFolders && Record.Category is { } cat
                ? Path.Combine(settings.DownloadDirectory, cat)
                : settings.DownloadDirectory;
        }

        Record.FileName = settings.Conflict == ConflictPolicy.Overwrite
            ? name
            : FileNameSanitizer.MakeUnique(Record.SaveDirectory, name);
    }

    private async Task<HttpResponseMessage> ProbeAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var req = HttpEngine.NewRequest(HttpMethod.Get, Http.Url, Http.Context);
                req.Headers.Range = new RangeHeaderValue(0, null);
                var resp = await SendWithTimeoutAsync(req, ct);
                var code = (int)resp.StatusCode;
                if (code is 200 or 206) return resp;
                resp.Dispose();
                if (code is 403 or 410) throw new StopException(DownloadState.NeedsLinkRefresh, "link-expired");
                if (code is not (429 or 503) && code < 500) throw new StopException(DownloadState.Failed, $"http-{code}");
                _lastError = $"http-{code}";
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                _lastError = "network";
                Log.Information("Download {Id} probe failed: {Error}", Record.Id, e.Message);
            }
            if (attempt >= MaxRetries) throw new StopException(DownloadState.Failed, _lastError ?? "network");
            await BackoffAsync(attempt, null, ct);
        }
    }

    // ================= workers =================

    private async Task DelayedWorkerAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { return; }
        if (!_throttled) await WorkerAsync(null, null, ct);
    }

    /// Worker accounting (_activeWorkers) changes only under _lock, together with the segment
    /// hand-off it belongs to, so a released segment always has a live worker to pick it up.
    private async Task WorkerAsync(HttpResponseMessage? response, Segment? seg, CancellationToken ct)
    {
        lock (_lock) _activeWorkers++;
        var retired = false;
        var failures = 0;
        try
        {
            await Task.Yield();
            seg ??= TakeWorkOrRetire(null);
            retired = seg is null;
            while (seg is not null)
            {
                try
                {
                    response ??= await RequestAsync(seg, ct);
                    await PumpAsync(response, seg, ct);
                    failures = 0;
                }
                catch (ThrottledException e)
                {
                    _throttled = true;
                    Rewind(seg);
                    if (TryRetireForThrottle(seg)) { retired = true; return; }
                    await BackoffAsync(++failures, e.RetryAfter, ct);
                }
                catch (LinkExpiredException)
                {
                    Rewind(seg);
                    if (++failures > 3 || !await ReresolveAsync(ct))
                        throw new StopException(DownloadState.NeedsLinkRefresh, "link-expired");
                }
                catch (Exception e) when (e is HttpRequestException or IOException && !DiskErrors.IsDiskFull(e))
                {
                    Rewind(seg);
                    _lastError = "network";
                    Log.Information(e, "Download {Id} segment {Seg} retry {N}", Record.Id, seg.Index, failures + 1);
                    if (++failures > MaxRetries) return; // finally retires and releases seg
                    await BackoffAsync(failures, null, ct);
                }
                finally
                {
                    response?.Dispose();
                    response = null;
                }

                if (seg.IsDone)
                {
                    seg = TakeWorkOrRetire(seg);
                    retired = seg is null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (StopException e) { RequestStop(e.State, e.Message); }
        catch (Exception e) when (DiskErrors.IsDiskFull(e)) { RequestStop(DownloadState.DiskFull, "disk-full"); }
        catch (Exception e)
        {
            Log.Error(e, "Download {Id} worker crashed", Record.Id);
            RequestStop(DownloadState.Failed, e.Message);
        }
        finally
        {
            response?.Dispose();
            if (!retired)
                lock (_lock)
                {
                    if (seg is not null) seg.Active = false;
                    _activeWorkers--;
                }
        }
    }

    /// Next piece of work, or null — in which case this worker is already counted out.
    private Segment? TakeWorkOrRetire(Segment? finished)
    {
        lock (_lock)
        {
            if (finished is not null) finished.Active = false;
            var next = _stopState is null ? SegmentPlanner.Take(_segments, Http.Resumable, MinSplit) : null;
            if (next is null) _activeWorkers--;
            return next;
        }
    }

    /// Drop received-but-unwritten bytes so the retry starts exactly where the file ends.
    private void Rewind(Segment seg)
    {
        lock (_lock)
        {
            if (!Http.Resumable) seg.Written = 0;  // no ranges: start over
            Interlocked.Add(ref _received, seg.Written - seg.Position);
            seg.Position = seg.Written;
        }
    }

    /// Server pushed back (429/503/refused): this worker leaves and hands its segment back,
    /// unless it is the last one — then it stays and backs off instead.
    private bool TryRetireForThrottle(Segment seg)
    {
        lock (_lock)
        {
            if (_activeWorkers <= 1) return false;
            seg.Active = false;
            _activeWorkers--;
            _connectionLimit = _activeWorkers;
            Log.Information("Download {Id}: server pushed back, connections → {N}", Record.Id, _connectionLimit);
            return true;
        }
    }

    private async Task<HttpResponseMessage> RequestAsync(Segment seg, CancellationToken ct)
    {
        var req = HttpEngine.NewRequest(HttpMethod.Get, Http.FinalUrl ?? Http.Url, Http.Context);
        if (Http.Resumable)
        {
            req.Headers.Range = new RangeHeaderValue(seg.Position, seg.End == long.MaxValue ? null : seg.End - 1);
            if (Http.ETag is { } etag) req.Headers.IfRange = new RangeConditionHeaderValue(etag);
            else if (Http.LastModified is { } lm) req.Headers.IfRange = new RangeConditionHeaderValue(lm);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await SendWithTimeoutAsync(req, ct);
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConnectionError && Volatile.Read(ref _activeWorkers) > 1)
        {
            throw new ThrottledException(null);   // refused: treat like 503
        }

        var code = (int)resp.StatusCode;
        if (code == 206 && Http.Resumable)
        {
            var range = resp.Content.Headers.ContentRange;
            if (range?.From == seg.Position && (range.Length is null || range.Length == Record.TotalBytes)) return resp;
            resp.Dispose();
            if (range?.Length is { } len && len != Record.TotalBytes) throw new StopException(DownloadState.Failed, "file-changed");
            throw new IOException("unexpected Content-Range");
        }
        if (code == 200 && !Http.Resumable) return resp;

        var retryAfter = resp.Headers.RetryAfter?.Delta;
        resp.Dispose();
        throw code switch
        {
            200 or 416 => new StopException(DownloadState.Failed, "file-changed"), // If-Range failed → full body
            403 or 410 => new LinkExpiredException(),
            429 or 503 => new ThrottledException(retryAfter),
            >= 500 => new HttpRequestException($"http-{code}"),
            _ => new StopException(DownloadState.Failed, $"http-{code}"),
        };
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StallTimeout);
        try
        {
            return await _engine.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("request timed out");
        }
    }

    private async Task PumpAsync(HttpResponseMessage resp, Segment seg, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var fill = 0;
        long writeAt;
        lock (_lock) writeAt = seg.Written;
        try
        {
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(fill, Math.Min(ReadChunk, buffer.Length - fill)), stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("connection stalled");
                }

                bool end;
                lock (_lock)
                {
                    if (n == 0)
                    {
                        if (seg.End == long.MaxValue) { seg.End = seg.Position; Record.TotalBytes = seg.End; }
                        end = true;
                    }
                    else
                    {
                        n = (int)Math.Min(n, seg.End - seg.Position); // segment may have been split meanwhile
                        seg.Position += n;
                        end = seg.Position >= seg.End;
                    }
                }
                fill += n;
                Interlocked.Add(ref _received, n);

                if (fill == buffer.Length || end)
                {
                    await RandomAccess.WriteAsync(_file!, buffer.AsMemory(0, fill), writeAt, CancellationToken.None);
                    writeAt += fill;
                    fill = 0;
                    lock (_lock) seg.Written = writeAt;
                }

                if (end)
                {
                    if (!seg.IsDone) throw new IOException("connection closed early");
                    return;
                }

                await _engine.GlobalBucket.AcquireAsync(n, ct);
                await _bucket.AcquireAsync(n, ct);
            }
        }
        finally
        {
            if (fill > 0)
            {
                try
                {
                    await RandomAccess.WriteAsync(_file!, buffer.AsMemory(0, fill), writeAt, CancellationToken.None);
                    lock (_lock) seg.Written = writeAt + fill;
                }
                catch (IOException) { } // Rewind() re-downloads it
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<bool> ReresolveAsync(CancellationToken ct)
    {
        await _reresolveGate.WaitAsync(ct);
        try
        {
            if (_lastReresolve != 0 && Stopwatch.GetElapsedTime(_lastReresolve) < TimeSpan.FromSeconds(10)) return true;
            var check = await CheckNewLinkAsync(Http.Url, null, ct);
            if (!check.Reachable || !check.SizeMatches) return false;
            Http.FinalUrl = check.FinalUrl;
            _lastReresolve = Stopwatch.GetTimestamp();
            Log.Information("Download {Id}: re-resolved redirect", Record.Id);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return false;
        }
        finally
        {
            _reresolveGate.Release();
        }
    }

    private Task BackoffAsync(int attempt, TimeSpan? hint, CancellationToken ct)
    {
        var baseDelay = hint ?? BackoffUnit * Math.Min(30, Math.Pow(2, attempt - 1));
        return Task.Delay(baseDelay * (0.5 + Random.Shared.NextDouble()), ct);
    }

    private void RequestStop(DownloadState state, string? error)
    {
        lock (_lock)
        {
            if (_stopState is not null) return;
            _stopState = state;
            _stopError = error;
        }
        _stop?.Cancel();
    }

    // ================= checkpoint / finish =================

    private async Task CheckpointLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { Checkpoint(); }
                catch (Exception e) { Log.Warning(e, "Checkpoint failed for {Id}", Record.Id); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// Order matters: snapshot positions, flush data to disk, then persist positions.
    /// The DB can therefore never claim bytes that are not on disk.
    private void Checkpoint()
    {
        lock (_checkpointLock)
        {
            if (_file is null || _file.IsClosed) return;
            SegmentRow[] rows;
            long done;
            lock (_lock)
            {
                rows = [.. _segments.Select(s => s.ToRow())];
                done = _segments.Sum(s => s.Written - s.Start);
            }
            Native.Flush(_file);
            Record.DoneBytes = done;
            _db.SaveSegments(Record.Id, done, rows);
        }
    }

    private async Task FinishAsync(bool paused)
    {
        bool complete;
        lock (_lock) complete = _stopState is null && _segments.Count > 0 && _segments.All(s => s.Written >= s.End);

        try { Checkpoint(); }
        catch (Exception e) { Log.Warning(e, "Final checkpoint failed for {Id}", Record.Id); }

        if (complete)
        {
            try
            {
                await CompleteAsync();
                SetState(DownloadState.Completed, null);
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, "Completing {Id} failed", Record.Id);
                _stopState = DownloadState.Failed;
                _stopError = e is StopException ? e.Message : "finalize: " + e.Message;
            }
        }

        lock (_checkpointLock) { _file?.Dispose(); _file = null; }
        if (_stopState is { } s) SetState(s, _stopError);
        else if (paused) SetState(DownloadState.Paused, null);
        else SetState(DownloadState.Failed, _lastError ?? "network");
    }

    private async Task CompleteAsync()
    {
        var part = PartPath;
        lock (_checkpointLock)
        {
            Native.Flush(_file!);
            _file!.Dispose();
            _file = null;
        }

        if (Http.HashAlgorithm is { } algo && Http.HashExpected is { } expected)
        {
            SetState(DownloadState.Verifying, null);
            await using var fs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = algo.Equals("MD5", StringComparison.OrdinalIgnoreCase) ? await MD5.HashDataAsync(fs) : await SHA256.HashDataAsync(fs);
            if (!Convert.ToHexString(hash).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new StopException(DownloadState.Failed, "hash-mismatch");
        }

        if (File.Exists(Record.FullPath))
        {
            if (_settings().Conflict == ConflictPolicy.Overwrite) File.Delete(Record.FullPath);
            else Record.FileName = FileNameSanitizer.MakeUnique(Record.SaveDirectory, Record.FileName);
        }
        File.Move(part, Record.FullPath);
        MarkOfTheWeb.Apply(Record.FullPath, Http.FinalUrl ?? Http.Url, Http.Referrer);

        Record.TotalBytes ??= Interlocked.Read(ref _received);
        Record.DoneBytes = Record.TotalBytes.Value;
        Record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _db.SaveSegments(Record.Id, Record.DoneBytes, []);
    }

    private void SetState(DownloadState state, string? error)
    {
        Record.State = state;
        Record.Error = error;
        _db.Update(Record);
        StateChanged?.Invoke(this);
    }

    public void DeleteFiles()
    {
        TryDelete(PartPath);
        if (Record.State == DownloadState.Completed) TryDelete(Record.FullPath);
        static void TryDelete(string p) { try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ================= link refresh =================

    /// Probes a replacement URL (manual paste or matched browser download).
    public async Task<LinkCheck> CheckNewLinkAsync(string url, RequestContext? ctx, CancellationToken ct)
    {
        var req = HttpEngine.NewRequest(HttpMethod.Get, url, ctx ?? Http.Context);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await SendWithTimeoutAsync(req, ct);
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            return new LinkCheck(false, false, false, url, null, null);

        var size = resp.StatusCode == HttpStatusCode.PartialContent ? resp.Content.Headers.ContentRange?.Length : resp.Content.Headers.ContentLength;
        var etag = resp.Headers.ETag is { IsWeak: false } e ? e.Tag : null;
        var lm = resp.Content.Headers.LastModified;
        var sizeMatches = Record.TotalBytes is null || size == Record.TotalBytes;
        var changed = (Http.ETag is not null && etag is not null && etag != Http.ETag)
                      || (Http.LastModified is not null && lm is not null && lm != Http.LastModified);
        return new LinkCheck(true, sizeMatches, changed, resp.RequestMessage?.RequestUri?.ToString() ?? url, etag, lm);
    }

    /// Caller must have checked SizeMatches. The download must not be running.
    public void ApplyNewLink(string url, RequestContext? ctx, LinkCheck check)
    {
        Http.Url = url;
        Http.FinalUrl = check.FinalUrl;
        Http.ETag = check.ETag;
        Http.LastModified = check.LastModified;
        if (ctx is not null)
        {
            Http.Referrer = ctx.Referrer ?? Http.Referrer;
            Http.UserAgent = ctx.UserAgent ?? Http.UserAgent;
            Http.Cookies = ctx.Cookies ?? Http.Cookies;
        }
        _lastReresolve = 0;
        _db.Update(Record);
    }

    private sealed class StopException(DownloadState state, string code) : Exception(code)
    {
        public DownloadState State { get; } = state;
    }

    private sealed class LinkExpiredException : Exception;

    private sealed class ThrottledException(TimeSpan? retryAfter) : Exception
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}

public sealed record LinkCheck(bool Reachable, bool SizeMatches, bool ValidatorChanged, string FinalUrl, string? ETag, DateTimeOffset? LastModified);

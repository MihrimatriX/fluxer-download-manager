using Fluxer.Core.Storage;
using Serilog;

namespace Fluxer.Core.Downloads;

/// Owns every download and decides which ones run. HTTP (incl. media) and torrents have separate
/// slot pools so a torrent hunting for peers never blocks HTTP downloads; seeding holds no slot.
public sealed class DownloadQueue(Database db, Func<AppSettings> settings, Func<DownloadRecord, IDownload> factory)
{
    private readonly Lock _lock = new();
    private readonly List<IDownload> _items = [];
    private readonly Dictionary<long, (CancellationTokenSource Cts, Task Task)> _running = [];
    private bool _hadWork;
    private bool _held;
    /// Named queues started (by their time or by hand) whose downloads have not all finished yet.
    private readonly HashSet<string> _startedQueues = [];
    /// Started queues that ran at least one download since they started.
    private readonly HashSet<string> _queuesThatRan = [];

    public event Action<IDownload>? Added;
    public event Action<IDownload>? Removed;
    public event Action<IDownload>? StateChanged;
    /// Queue order changed (no state changed); listeners refresh queue positions.
    public event Action? Reordered;
    /// Raised once when the last running/queued download finishes (for "sleep/shutdown when done").
    public event Action? Drained;
    /// A started named queue has nothing left to run (its "when done" action).
    public event Action<string>? QueueDrained;

    public bool IsStarted(string queueId) { lock (_lock) return _startedQueues.Contains(queueId); }

    public IReadOnlyList<IDownload> Items { get { lock (_lock) return [.. _items]; } }

    public IDownload? Find(long id) { lock (_lock) return _items.FirstOrDefault(d => d.Id == id); }

    public bool IsRunning(long id) { lock (_lock) return _running.ContainsKey(id); }

    public bool AnyActive { get { lock (_lock) return _running.Count > 0; } }

    public int ActiveMediaCount
    {
        get { lock (_lock) return _items.Count(d => d.Kind == DownloadKind.Media && _running.ContainsKey(d.Id)); }
    }

    /// Loads persisted downloads; interrupted ones go back to the queue.
    public void Load()
    {
        foreach (var r in db.LoadAll())
        {
            if (r.State is DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.Seeding)
            {
                r.State = DownloadState.Queued;
                db.Update(r);
            }
            // Waiting to run (not Scheduled) in a named queue: that queue was running when Fluxer closed.
            if (r is { State: DownloadState.Queued, QueueId: { } q }) lock (_lock) _startedQueues.Add(q);
            Track(factory(r));
        }
        Schedule();
    }

    /// `night`: waits in the night queue until <see cref="StartNight"/>. A record with a QueueId waits for that
    /// queue (or joins it at once when it is running).
    public IDownload Add(DownloadRecord record, bool start = true, bool night = false)
    {
        record.State = record.QueueId is { } q ? (IsStarted(q) ? DownloadState.Queued : DownloadState.Scheduled)
            : night ? DownloadState.Scheduled : start ? DownloadState.Queued : DownloadState.Paused;
        db.Insert(record);
        var d = Track(factory(record));
        Added?.Invoke(d);
        Schedule();
        return d;
    }

    /// An imported download keeps its state (paused, or completed as history) and does not start.
    public IDownload AddImported(DownloadRecord record)
    {
        if (record.State != DownloadState.Completed) record.State = DownloadState.Paused;
        db.Insert(record);
        var d = Track(factory(record));
        Added?.Invoke(d);
        return d;
    }

    /// Persists a record the UI changed (tags).
    public void Save(IDownload d) => db.Update(d.Record);

    public void Resume(long id)
    {
        var d = Find(id);
        if (d is null || IsRunning(id) || d.State == DownloadState.Completed) return;
        SetIdleState(d, DownloadState.Queued);
        Schedule();
    }

    public async Task PauseAsync(long id)
    {
        Task? task = null;
        IDownload? d;
        lock (_lock)
        {
            d = _items.FirstOrDefault(x => x.Id == id);
            if (_running.TryGetValue(id, out var run)) { run.Cts.Cancel(); task = run.Task; }
        }
        if (task is not null) await task;
        else if (d is { State: DownloadState.Queued or DownloadState.Scheduled }) SetIdleState(d, DownloadState.Paused);
    }

    /// The night queue keeps waiting for its own start time.
    public async Task PauseAllAsync()
    {
        foreach (var d in Items) if (d.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Scheduled)) await PauseAsync(d.Id);
    }

    /// Moves an idle download to the night queue (null) or a named queue, where it waits for that queue's start.
    public void Defer(long id, string? queueId = null)
    {
        if (Find(id) is not { } d || IsRunning(id) || d.State == DownloadState.Completed) return;
        d.Record.QueueId = queueId;
        SetIdleState(d, queueId is not null && IsStarted(queueId) ? DownloadState.Queued : DownloadState.Scheduled);
        Schedule();
    }

    /// Back to the main queue: waits its turn there.
    public void MoveToMain(long id)
    {
        if (Find(id) is not { } d || d.State == DownloadState.Completed) return;
        d.Record.QueueId = null;
        if (d.State == DownloadState.Scheduled) SetIdleState(d, DownloadState.Queued);
        else db.Update(d.Record);
        Schedule();
    }

    /// Night queue start: everything waiting in it joins the normal queue.
    public void StartNight() => StartQueue(null);

    /// A queue starts: its waiting downloads join the run, named ones with that queue's own slot limit.
    public void StartQueue(string? queueId)
    {
        if (queueId is not null) lock (_lock) _startedQueues.Add(queueId);
        foreach (var d in Items) if (d.State == DownloadState.Scheduled && d.Record.QueueId == queueId) SetIdleState(d, DownloadState.Queued);
        Schedule();
    }

    /// A named queue's stop time: what has not finished waits for its next start.
    public async Task StopQueueAsync(string queueId)
    {
        lock (_lock) { _startedQueues.Remove(queueId); _queuesThatRan.Remove(queueId); }
        foreach (var d in Items.Where(d => d.Record.QueueId == queueId && (IsRunning(d.Id) || d.State == DownloadState.Queued)))
        {
            await PauseAsync(d.Id);
            if (d.State is DownloadState.Paused or DownloadState.Queued) SetIdleState(d, DownloadState.Scheduled);
        }
    }

    /// Smart speed (low battery, traffic quota): nothing starts while held, and what was running waits its turn
    /// again, so it continues by itself when the hold ends.
    public bool Held { get { lock (_lock) return _held; } }

    public async Task HoldAsync(bool on)
    {
        lock (_lock)
        {
            if (_held == on) return;
            _held = on;
        }
        if (!on) { Schedule(); return; }
        foreach (var d in Items.Where(d => IsRunning(d.Id)))
        {
            // A torrent stopped while seeding reports Completed; it is to seed again after the hold too.
            var wasSeeding = d.State == DownloadState.Seeding;
            await PauseAsync(d.Id);
            if (d.State == DownloadState.Paused || (wasSeeding && d.State == DownloadState.Completed)) SetIdleState(d, DownloadState.Queued);
        }
        if (!Held) Schedule();      // released while these were still stopping
    }

    /// Stops a download for work on its files (torrent recheck or move). True when it should run again afterwards:
    /// it was running or waiting its turn, seeding included.
    public async Task<bool> SuspendAsync(long id)
    {
        if (Find(id) is not { } d) return false;
        var again = IsRunning(id) || d.State == DownloadState.Queued;
        await PauseAsync(id);
        return again;
    }

    /// Puts a suspended download back in line, also a torrent that stopped seeding as Completed.
    public void Continue(long id)
    {
        if (Find(id) is not { } d || IsRunning(id)) return;
        SetIdleState(d, DownloadState.Queued);
        Schedule();
    }

    public void ResumeAll()
    {
        foreach (var d in Items) if (d.State is DownloadState.Paused or DownloadState.DiskFull) Resume(d.Id);
    }

    public async Task RemoveAsync(long id, bool deleteFiles)
    {
        var d = Find(id);
        if (d is null) return;
        await PauseAsync(id);
        if (d is Torrent.TorrentDownload t) await t.DetachAsync();
        if (deleteFiles) d.DeleteFiles();
        db.Delete(id);
        lock (_lock) _items.Remove(d);
        d.StateChanged -= OnStateChanged;
        Removed?.Invoke(d);
        Schedule();
    }

    public enum Move { Top, Up, Down, Bottom }

    /// Reorders the queue (what starts next). Moves the given downloads as a block, keeping their order.
    public void Reorder(IReadOnlyCollection<long> ids, Move move)
    {
        lock (_lock)
        {
            var ordered = _items.OrderBy(d => d.Record.QueueOrder).ToList();
            var picked = ordered.Where(d => ids.Contains(d.Id)).ToList();
            if (picked.Count == 0) return;
            var first = ordered.IndexOf(picked[0]);
            var rest = ordered.Except(picked).ToList();
            var at = move switch
            {
                Move.Top => 0,
                Move.Bottom => rest.Count,
                Move.Up => Math.Max(0, first - 1),
                _ => Math.Min(rest.Count, first + 1),
            };
            rest.InsertRange(at, picked);
            Renumber(rest);
        }
        Reordered?.Invoke();
        Schedule();
    }

    /// Puts a download at `index` of the queue order: before the one there when moving up, after it when moving down.
    public void MoveTo(long id, int index)
    {
        lock (_lock)
        {
            var ordered = _items.OrderBy(d => d.Record.QueueOrder).ToList();
            if (ordered.FirstOrDefault(x => x.Id == id) is not { } d) return;
            ordered.Remove(d);
            ordered.Insert(Math.Clamp(index, 0, ordered.Count), d);
            Renumber(ordered);
        }
        Reordered?.Invoke();
        Schedule();
    }

    private void Renumber(List<IDownload> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Record.QueueOrder == i + 1) continue;
            ordered[i].Record.QueueOrder = i + 1;
            db.Update(ordered[i].Record);
        }
    }

    /// 1-based position among queued downloads (null when not queued).
    public int? QueuePosition(IDownload d)
    {
        if (d.State != DownloadState.Queued) return null;
        lock (_lock)
            return 1 + _items.Count(x => x.State == DownloadState.Queued && x.Record.QueueOrder < d.Record.QueueOrder);
    }

    /// The download that already uses this link (as its address, final address or page), if any.
    public IDownload? FindByUrl(string url)
    {
        url = url.Trim();
        lock (_lock)
            return _items.FirstOrDefault(d => d.Record.Http is { } h && (h.Url == url || h.FinalUrl == url)
                                              || d.Record.Media?.PageUrl == url);
    }

    /// Downloads waiting for a fresh link whose name (and size, when known) match.
    public IEnumerable<IDownload> FindRefreshCandidates(string fileName, long? size)
    {
        var name = Platform.Windows.FileNameSanitizer.Sanitize(fileName);
        return Items.Where(d => d.State == DownloadState.NeedsLinkRefresh
                                && NameMatches(d.FileName, name)
                                && (size is null || d.Record.TotalBytes is null || d.Record.TotalBytes == size));

        static bool NameMatches(string current, string incoming)
        {
            if (current.Equals(incoming, StringComparison.OrdinalIgnoreCase)) return true;
            // "file (1).zip" was uniquified from "file.zip"
            var stem = Path.GetFileNameWithoutExtension(current);
            var paren = stem.LastIndexOf(" (", StringComparison.Ordinal);
            return paren > 0 && stem.EndsWith(')')
                   && (stem[..paren] + Path.GetExtension(current)).Equals(incoming, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Schedule()
    {
        bool drained;
        List<string> doneQueues = [];
        lock (_lock)
        {
            var s = settings();
            int http = 0, torrent = 0;
            var perQueue = new Dictionary<string, int>();
            foreach (var (id, _) in _running)
            {
                var d = _items.FirstOrDefault(x => x.Id == id);
                if (d is null || !d.OccupiesSlot && d.State != DownloadState.Queued) continue;
                if (d.Kind == DownloadKind.Torrent) torrent++; else http++;
                if (d.Record.QueueId is { } q) perQueue[q] = perQueue.GetValueOrDefault(q) + 1;
            }

            foreach (var d in _items.Where(x => !_held && x.State == DownloadState.Queued && !_running.ContainsKey(x.Id)).OrderBy(x => x.Record.QueueOrder))
            {
                var isTorrent = d.Kind == DownloadKind.Torrent;
                if (isTorrent ? torrent >= s.TorrentSlots : http >= s.HttpSlots) continue;
                if (d.Record.QueueId is { } q && s.Queues.FirstOrDefault(x => x.Id == q) is { Slots: > 0 } own && perQueue.GetValueOrDefault(q) >= own.Slots) continue;
                if (isTorrent) torrent++; else http++;
                if (d.Record.QueueId is { } q2) perQueue[q2] = perQueue.GetValueOrDefault(q2) + 1;
                Start(d);
            }

            drained = _hadWork && http == 0 && torrent == 0 && !_items.Any(x => x.State == DownloadState.Queued);
            if (drained) _hadWork = false;
            // A queue is done when nothing of it waits or runs. Its "when done" action (sleep, shut down) only follows
            // a run that did something and that the user did not interrupt: not an empty start, not a pause.
            foreach (var q in _startedQueues.ToList())
            {
                if (_items.Any(x => x.Record.QueueId == q && (x.State == DownloadState.Queued || _running.ContainsKey(x.Id)))) continue;
                _startedQueues.Remove(q);
                var ran = _queuesThatRan.Remove(q);
                var interrupted = _items.Any(x => x.Record.QueueId == q
                    && x.State is DownloadState.Paused or DownloadState.NeedsLinkRefresh or DownloadState.DiskFull);
                if (ran && !interrupted) doneQueues.Add(q);
            }
        }
        foreach (var q in doneQueues) QueueDrained?.Invoke(q);
        if (drained) Drained?.Invoke();
    }

    private void Start(IDownload d)
    {
        _hadWork = true;
        if (d.Record.QueueId is { } q && _startedQueues.Contains(q)) _queuesThatRan.Add(q);
        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try { await d.RunAsync(cts.Token); }
            catch (Exception e) { Log.Error(e, "Download {Id} crashed", d.Id); }
            finally
            {
                lock (_lock) _running.Remove(d.Id);
                cts.Dispose();
                Schedule();
            }
        });
        _running[d.Id] = (cts, task);
    }

    private IDownload Track(IDownload d)
    {
        d.StateChanged += OnStateChanged;
        lock (_lock) _items.Add(d);
        return d;
    }

    private void OnStateChanged(IDownload d)
    {
        StateChanged?.Invoke(d);
        if (d.State == DownloadState.Seeding) Schedule(); // seeding frees the torrent slot
    }

    private void SetIdleState(IDownload d, DownloadState state)
    {
        d.Record.State = state;
        d.Record.Error = null;
        db.Update(d.Record);
        StateChanged?.Invoke(d);
    }

    public async Task StopAllAsync()
    {
        Task[] tasks;
        lock (_lock)
        {
            foreach (var (_, run) in _running) run.Cts.Cancel();
            tasks = [.. _running.Values.Select(r => r.Task)];
        }
        await Task.WhenAll(tasks);
    }
}

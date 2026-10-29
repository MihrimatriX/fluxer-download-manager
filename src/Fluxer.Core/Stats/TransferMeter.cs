namespace Fluxer.Core.Stats;

/// Bytes moved, per minute for the last day (traffic quota) and per day for good (statistics). Fed with each
/// download's running totals; only growth counts, so pausing, removing or restarting one never goes negative.
public sealed class TransferMeter
{
    private readonly Dictionary<long, (long Down, long Up)> _last = [];
    private readonly List<(long Minute, long Down, long Up)> _minutes = [];
    private readonly Lock _lock = new();

    /// Today's totals waiting to be written (the app flushes them every few minutes and on exit).
    public long PendingDown { get; private set; }
    public long PendingUp { get; private set; }

    public void Observe(IEnumerable<(long Id, long Down, long Up)> downloads, DateTimeOffset now)
    {
        long down = 0, up = 0;
        lock (_lock)
        {
            var seen = new HashSet<long>();
            foreach (var (id, d, u) in downloads)
            {
                seen.Add(id);
                if (_last.TryGetValue(id, out var prev))
                {
                    down += Math.Max(0, d - prev.Down);
                    up += Math.Max(0, u - prev.Up);
                }
                _last[id] = (d, u);
            }
            foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
            if (down == 0 && up == 0) return;

            var minute = now.ToUnixTimeSeconds() / 60;
            if (_minutes.Count > 0 && _minutes[^1].Minute == minute) _minutes[^1] = (minute, _minutes[^1].Down + down, _minutes[^1].Up + up);
            else _minutes.Add((minute, down, up));
            _minutes.RemoveAll(m => m.Minute < minute - 24 * 60);
            PendingDown += down;
            PendingUp += up;
        }
    }

    /// Downloaded in the last `window` (at most a day back).
    public long DownWithin(TimeSpan window, DateTimeOffset now)
    {
        var from = now.ToUnixTimeSeconds() / 60 - (long)window.TotalMinutes;
        lock (_lock) return _minutes.Where(m => m.Minute > from).Sum(m => m.Down);
    }

    /// When the oldest minute still counted in `window` leaves it (quota: when downloading may resume).
    public DateTimeOffset? WindowFreesAt(TimeSpan window, DateTimeOffset now)
    {
        var from = now.ToUnixTimeSeconds() / 60 - (long)window.TotalMinutes;
        lock (_lock)
        {
            foreach (var m in _minutes)
                if (m.Minute > from && m.Down > 0) return DateTimeOffset.FromUnixTimeSeconds((m.Minute + 1) * 60) + window;
            return null;
        }
    }

    /// The last day's minutes, kept across restarts so quitting does not reset the quota.
    public TrafficLog Snapshot()
    {
        lock (_lock) return new TrafficLog { Minutes = [.. _minutes.Select(m => new[] { m.Minute, m.Down, m.Up })] };
    }

    public void Restore(TrafficLog log, DateTimeOffset now)
    {
        var from = now.ToUnixTimeSeconds() / 60 - 24 * 60;
        lock (_lock)
        {
            _minutes.Clear();
            _minutes.AddRange(log.Minutes.Where(m => m.Length == 3 && m[0] >= from).Select(m => (m[0], m[1], m[2])).OrderBy(m => m.Item1));
        }
    }

    public (long Down, long Up) TakePending()
    {
        lock (_lock)
        {
            var r = (PendingDown, PendingUp);
            PendingDown = PendingUp = 0;
            return r;
        }
    }
}

/// Saved form of the meter's minutes: [minute, down, up].
public sealed class TrafficLog
{
    public List<long[]> Minutes { get; set; } = [];
}

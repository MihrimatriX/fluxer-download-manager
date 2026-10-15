using System.Diagnostics;

namespace Fluxer.Core.Downloads;

/// Exponentially smoothed bytes/second. Sampling at any rate works: the time delta is honoured.
public sealed class SpeedMeter
{
    private readonly Lock _lock = new();
    private long _lastTicks;
    private long _lastBytes;
    private double _speed;

    public long Sample(long totalBytes)
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastTicks == 0) { _lastTicks = now; _lastBytes = totalBytes; return 0; }
            var dt = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds;
            if (dt < 0.2) return (long)_speed;
            var inst = Math.Max(0, totalBytes - _lastBytes) / dt;
            var alpha = Math.Min(1, dt / 1.5); // ~1.5 s time constant
            _speed += (inst - _speed) * alpha;
            _lastTicks = now;
            _lastBytes = totalBytes;
            return (long)_speed;
        }
    }

    public void Reset() { lock (_lock) { _lastTicks = 0; _speed = 0; } }

    public static TimeSpan? Eta(long done, long? total, long speed) =>
        total is { } t && speed > 0 && t > done ? TimeSpan.FromSeconds((t - done) / (double)speed) : null;
}

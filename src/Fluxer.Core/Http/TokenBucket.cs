using System.Diagnostics;

namespace Fluxer.Core.Http;

/// Bytes/second limiter. Capacity = one second of rate. Callers may overdraw; they then
/// sleep until the debt is repaid, so concurrent callers share the rate fairly. 0 = unlimited.
public sealed class TokenBucket
{
    private readonly Lock _lock = new();
    private long _rate;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();

    public long BytesPerSecond
    {
        get => Volatile.Read(ref _rate);
        set { lock (_lock) { _rate = Math.Max(0, value); _tokens = Math.Min(_tokens, _rate); } }
    }

    public ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        if (Volatile.Read(ref _rate) == 0) return ValueTask.CompletedTask;
        double debt;
        lock (_lock)
        {
            if (_rate == 0) return ValueTask.CompletedTask;
            var now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(_rate, _tokens + Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds * _rate);
            _lastTicks = now;
            _tokens -= bytes;
            debt = -_tokens / _rate;
        }
        return debt <= 0 ? ValueTask.CompletedTask : new ValueTask(Task.Delay(TimeSpan.FromSeconds(debt), ct));
    }
}

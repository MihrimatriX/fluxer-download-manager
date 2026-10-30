namespace Fluxer.Core.Platform.Windows;

/// Keeps the system awake while `isActive` returns true. SetThreadExecutionState is per-thread,
/// so instead of ES_CONTINUOUS (which dies with the thread-pool thread) we poke the idle timer every 30 s.
public sealed class SleepBlocker(Func<bool> isActive) : IDisposable
{
    private readonly Timer _timer = new(_ => { if (isActive()) Native.PokeSystemAwake(); }, null,
        TimeSpan.Zero, TimeSpan.FromSeconds(30));

    public void Dispose() => _timer.Dispose();
}

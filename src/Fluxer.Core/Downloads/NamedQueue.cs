using System.Globalization;

namespace Fluxer.Core.Downloads;

/// A queue of the user's own besides the main and night queues: its downloads wait until it starts (at its time
/// on its days, or by hand), at most Slots of them run at once, and when the last one is done it can sleep or
/// shut down the PC.
public sealed class NamedQueue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string? Start { get; set; }                  // "HH:mm", null = only by hand
    public string? Stop { get; set; }                   // "HH:mm": unfinished downloads wait for the next start
    public string Days { get; set; } = "1111111";       // Monday first
    public int Slots { get; set; }                      // 0 = only the global limit
    public string AfterDone { get; set; } = "None";     // None | Sleep | Shutdown

    public bool RunsOn(DateTime day)
    {
        var i = ((int)day.DayOfWeek + 6) % 7;           // DayOfWeek starts on Sunday
        return Days.Length == 7 && Days[i] == '1';
    }

    /// The next time it starts on its own, for "Starts Mon 09:00"; null when it only starts by hand.
    public DateTime? NextStart(DateTime now)
    {
        if (!TimeOnly.TryParseExact(Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        for (var d = 0; d < 8; d++)
        {
            var day = now.Date.AddDays(d);
            var at = day + t.ToTimeSpan();
            if (at > now && RunsOn(day)) return at;
        }
        return null;
    }
}

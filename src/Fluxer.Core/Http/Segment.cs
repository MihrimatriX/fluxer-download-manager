namespace Fluxer.Core.Http;

/// Byte range [Start, End). All mutation happens under the owning download's lock.
/// Position = bytes received (may sit in a buffer); Written = bytes known to be in the file.
/// Splitting uses Position, checkpoints use Written, so neither overlaps nor loses data.
public sealed class Segment(int index, long start, long end)
{
    public int Index { get; } = index;
    public long Start { get; } = start;
    public long End { get; set; } = end;
    public long Position { get; set; } = start;
    public long Written { get; set; } = start;
    public bool Active { get; set; }

    public long Remaining => End - Position;
    public bool IsDone => Position >= End;

    public SegmentRow ToRow() => new(Index, Start, End, Written);

    public static Segment FromRow(SegmentRow r) => new(r.Index, r.Start, r.End) { Position = r.Position, Written = r.Position };
}

public readonly record struct SegmentRow(int Index, long Start, long End, long Position);

public static class SegmentPlanner
{
    public const long DefaultMinSplit = 1024 * 1024;

    /// Hands a connection its next piece of work. Caller holds the download lock.
    /// 1) an idle unfinished segment (resume, or one released by a failed connection);
    /// 2) otherwise split the active segment with the most bytes left at its midpoint.
    /// Returns null when nothing is worth a new connection.
    public static Segment? Take(List<Segment> segments, bool allowSplit, long minSplit = DefaultMinSplit)
    {
        Segment? idle = null, victim = null;
        foreach (var s in segments)
        {
            if (s.IsDone) continue;
            if (!s.Active) { if (idle is null || s.Remaining > idle.Remaining) idle = s; }
            else if (victim is null || s.Remaining > victim.Remaining) victim = s;
        }

        if (idle is not null) { idle.Active = true; return idle; }
        if (!allowSplit || victim is null || victim.End == long.MaxValue || victim.Remaining < 2 * minSplit) return null;

        var mid = victim.Position + victim.Remaining / 2;
        var piece = new Segment(segments.Max(s => s.Index) + 1, mid, victim.End) { Active = true };
        victim.End = mid;
        segments.Add(piece);
        return piece;
    }
}

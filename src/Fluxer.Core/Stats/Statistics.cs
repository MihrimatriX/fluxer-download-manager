using Fluxer.Core.Downloads;

namespace Fluxer.Core.Stats;

public sealed record DayTotal(DateOnly Day, long Down, long Up, int Files);

/// A site or category with what came from it.
public sealed record Ranked(string Name, long Bytes, int Count);

/// What the statistics window shows: totals over periods, the last year day by day, and where the downloads in
/// the list came from.
public sealed record StatsReport(
    long Today, long Week, long Month, long AllTime, long UploadedAllTime, int FilesToday, int FilesAllTime,
    IReadOnlyList<DayTotal> Year, IReadOnlyList<Ranked> Sites, IReadOnlyList<Ranked> Categories)
{
    /// Days of the last 53 weeks, Monday-first and ending with the week of `today` (days after today are zero).
    public static StatsReport Build(IReadOnlyList<DayTotal> days, IEnumerable<DownloadRecord> records, DateOnly today, int top = 8)
    {
        var byDay = days.ToDictionary(d => d.Day);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var first = monday.AddDays(-52 * 7);
        var year = Enumerable.Range(0, 53 * 7).Select(i => first.AddDays(i))
            .Select(d => byDay.GetValueOrDefault(d) ?? new DayTotal(d, 0, 0, 0)).ToList();
        long Sum(DateOnly from) => days.Where(d => d.Day >= from && d.Day <= today).Sum(d => d.Down);

        var done = records.Where(r => r.State == DownloadState.Completed || r.Kind == DownloadKind.Torrent && r.State == DownloadState.Seeding).ToList();
        static long Size(DownloadRecord r) => r.TotalBytes ?? r.DoneBytes;
        var sites = done.GroupBy(Site, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Ranked(g.Key, g.Sum(Size), g.Count()))
            .OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.Ordinal).Take(top).ToList();
        var categories = done.GroupBy(r => r.Category ?? "", StringComparer.Ordinal)
            .Select(g => new Ranked(g.Key, g.Sum(Size), g.Count()))
            .OrderByDescending(x => x.Bytes).Take(top).ToList();

        return new StatsReport(
            Sum(today), Sum(monday), Sum(new DateOnly(today.Year, today.Month, 1)), days.Sum(d => d.Down), days.Sum(d => d.Up),
            byDay.GetValueOrDefault(today)?.Files ?? 0, days.Sum(d => d.Files), year, sites, categories);
    }

    /// "github.com" (www. dropped); torrents count as one "BitTorrent" source.
    public static string Site(DownloadRecord r)
    {
        var url = r.Http?.Url ?? r.Media?.PageUrl;
        if (r.Kind == DownloadKind.Torrent || url is null) return "BitTorrent";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "?";
        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}

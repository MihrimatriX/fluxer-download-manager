using Fluxer.Core.Downloads;
using Fluxer.Core.Stats;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class StatsTests
{
    private static readonly DateOnly Wednesday = new(2026, 10, 28);

    [Fact]
    public void Totals_ByPeriod_AndTheYearEndsWithThisWeek()
    {
        DayTotal[] days =
        [
            new(new DateOnly(2025, 12, 31), 1000, 0, 1),
            new(new DateOnly(2026, 9, 30), 200, 0, 0),
            new(new DateOnly(2026, 10, 1), 30, 5, 0),
            new(new DateOnly(2026, 10, 26), 4, 0, 1),        // Monday
            new(Wednesday, 1, 7, 2),
        ];
        var r = StatsReport.Build(days, [], Wednesday);
        Assert.Equal(1, r.Today);
        Assert.Equal(5, r.Week);
        Assert.Equal(35, r.Month);
        Assert.Equal(1235, r.AllTime);
        Assert.Equal(12, r.UploadedAllTime);
        Assert.Equal((2, 4), (r.FilesToday, r.FilesAllTime));

        Assert.Equal(53 * 7, r.Year.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), r.Year[^7].Day);
        Assert.Equal(new DateOnly(2026, 11, 1), r.Year[^1].Day);
        Assert.Equal(1, r.Year[^5].Down);
        Assert.Equal(DayOfWeek.Monday, r.Year[0].Day.DayOfWeek);
    }

    [Fact]
    public void Sites_MergeWww_AndCountOnlyFinishedOnes()
    {
        DownloadRecord Http(string url, long size, DownloadState state = DownloadState.Completed, string? cat = null) =>
            new() { Kind = DownloadKind.Http, State = state, TotalBytes = size, Category = cat, Http = new HttpInfo { Url = url } };
        var records = new[]
        {
            Http("https://www.github.com/a.zip", 100, cat: "Archives"),
            Http("https://github.com/b.zip", 50, cat: "Archives"),
            Http("https://cdn.example.org/v.mp4", 120, cat: "Video"),
            Http("https://cdn.example.org/w.mp4", 900, DownloadState.Paused),
            new DownloadRecord { Kind = DownloadKind.Torrent, State = DownloadState.Seeding, TotalBytes = 10 },
        };
        var r = StatsReport.Build([], records, Wednesday);
        Assert.Equal([("github.com", 150L, 2), ("cdn.example.org", 120L, 1), ("BitTorrent", 10L, 1)], r.Sites.Select(s => (s.Name, s.Bytes, s.Count)));
        Assert.Equal(("Archives", 150L), (r.Categories[0].Name, r.Categories[0].Bytes));
    }

    [Fact]
    public void TrafficTable_AddsUpPerDay()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxer-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var db = new Storage.Database(Path.Combine(dir, "s.db")))
            {
                db.AddTraffic(Wednesday, 100, 1, 0);
                db.AddTraffic(Wednesday, 50, 0, 1);
                db.AddTraffic(Wednesday.AddDays(-1), 7, 0, 0);
                Assert.Equal([new DayTotal(Wednesday.AddDays(-1), 7, 0, 0), new DayTotal(Wednesday, 150, 1, 1)], db.LoadTraffic());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

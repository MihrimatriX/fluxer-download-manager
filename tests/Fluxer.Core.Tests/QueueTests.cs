using Fluxer.Core.Downloads;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class NamedQueueTests : IDisposable
{
    /// Runs until Finish() (or a pause), then reports Completed.
    private sealed class FakeDownload(DownloadRecord r) : IDownload
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DownloadRecord Record { get; } = r;
        public long Id => Record.Id;
        public DownloadKind Kind => Record.Kind;
        public DownloadState State => Record.State;
        public string FileName => Record.FileName;
        public event Action<IDownload>? StateChanged;

        public void Finish() => _done.TrySetResult();

        /// A name starting with "seed" behaves like a seeding torrent: stopping it reports Completed.
        public async Task RunAsync(CancellationToken ct)
        {
            var seeder = FileName.StartsWith("seed", StringComparison.Ordinal);
            Set(seeder ? DownloadState.Seeding : DownloadState.Downloading);
            try { await _done.Task.WaitAsync(ct); Set(DownloadState.Completed); }
            catch (OperationCanceledException) { Set(seeder ? DownloadState.Completed : DownloadState.Paused); }
        }

        private void Set(DownloadState s) { Record.State = s; StateChanged?.Invoke(this); }
        public DownloadProgress GetProgress() => default;
        public void DeleteFiles() { }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fluxer-queue-" + Guid.NewGuid().ToString("N"));
    private readonly Storage.Database _db;
    private readonly AppSettings _settings = new() { HttpSlots = 5 };
    private readonly DownloadQueue _queue;

    public NamedQueueTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Storage.Database(Path.Combine(_dir, "q.db"));
        _settings.Queues.Add(new NamedQueue { Id = "weekend", Name = "Weekend", Slots = 1 });
        _queue = new DownloadQueue(_db, () => _settings, r => new FakeDownload(r));
    }

    public void Dispose()
    {
        _queue.StopAllAsync().GetAwaiter().GetResult();
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private FakeDownload Add(string name, string? queue) =>
        (FakeDownload)_queue.Add(new DownloadRecord { Kind = DownloadKind.Http, FileName = name, QueueId = queue, Http = new HttpInfo { Url = "https://e.org/" + name } });

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task QueuedDownloads_WaitForTheirQueue_ThenRunOneAtATime()
    {
        var a = Add("a", "weekend");
        var b = Add("b", "weekend");
        var main = Add("m", null);
        await Until(() => _queue.IsRunning(main.Id));
        Assert.Equal((DownloadState.Scheduled, DownloadState.Scheduled), (a.State, b.State));

        _queue.StartQueue("weekend");
        await Until(() => _queue.IsRunning(a.Id));
        await Task.Delay(50);
        Assert.False(_queue.IsRunning(b.Id));       // the queue's own limit is 1

        a.Finish();
        await Until(() => _queue.IsRunning(b.Id));
    }

    [Fact]
    public async Task QueueDrained_FiresWhenItsLastDownloadIsDone()
    {
        var drained = new List<string>();
        _queue.QueueDrained += drained.Add;
        var a = Add("a", "weekend");
        _queue.StartQueue("weekend");
        await Until(() => _queue.IsRunning(a.Id));
        Assert.Empty(drained);
        a.Finish();
        await Until(() => drained.Count == 1);
        Assert.Equal("weekend", drained[0]);
        Assert.False(_queue.IsStarted("weekend"));
    }

    [Fact]
    public async Task QueueDrained_NotForAnEmptyStart_NorWhenTheUserPausedWhatRan()
    {
        var drained = new List<string>();
        _queue.QueueDrained += drained.Add;
        _queue.StartQueue("weekend");                // nothing in it: its "when done" (shutdown…) must not run
        await Task.Delay(50);
        Assert.Empty(drained);

        var a = Add("a", "weekend");
        _queue.StartQueue("weekend");
        await Until(() => _queue.IsRunning(a.Id));
        await _queue.PauseAsync(a.Id);
        await Task.Delay(50);
        Assert.Empty(drained);
    }

    [Fact]
    public async Task StopQueue_SendsUnfinishedOnesBackToWaiting()
    {
        var a = Add("a", "weekend");
        _queue.StartQueue("weekend");
        await Until(() => _queue.IsRunning(a.Id));
        await _queue.StopQueueAsync("weekend");
        Assert.Equal(DownloadState.Scheduled, a.State);
        Assert.Equal("weekend", _db.LoadAll().Single(r => r.Id == a.Id).QueueId);
    }

    [Fact]
    public async Task Defer_AndMoveToMain()
    {
        var a = Add("a", null);
        await Until(() => _queue.IsRunning(a.Id));
        await _queue.PauseAsync(a.Id);
        _queue.Defer(a.Id, "weekend");
        Assert.Equal((DownloadState.Scheduled, "weekend"), (a.State, a.Record.QueueId));
        _queue.MoveToMain(a.Id);
        Assert.Null(a.Record.QueueId);
        await Until(() => _queue.IsRunning(a.Id));
    }

    [Fact]
    public async Task Hold_StopsWhatRuns_AndNothingStartsUntilReleased()
    {
        var a = Add("a", null);
        await Until(() => _queue.IsRunning(a.Id));
        await _queue.HoldAsync(true);
        Assert.Equal(DownloadState.Queued, a.State);
        Assert.False(_queue.IsRunning(a.Id));

        var b = Add("b", null);
        await Task.Delay(50);
        Assert.False(_queue.IsRunning(b.Id));

        await _queue.HoldAsync(false);
        await Until(() => _queue.IsRunning(a.Id) && _queue.IsRunning(b.Id));
    }

    [Fact]
    public async Task Hold_ASeedingTorrentSeedsAgainAfterwards()
    {
        var seed = Add("seed.iso", null);
        await Until(() => seed.State == DownloadState.Seeding);
        await _queue.HoldAsync(true);
        Assert.Equal(DownloadState.Queued, seed.State);
        await _queue.HoldAsync(false);
        await Until(() => seed.State == DownloadState.Seeding);
    }

    [Fact]
    public async Task SuspendAndContinue_BringBackWhatRan()
    {
        var seed = Add("seed.iso", null);
        var idle = Add("idle.bin", null);
        await Until(() => seed.State == DownloadState.Seeding && _queue.IsRunning(idle.Id));
        await _queue.PauseAsync(idle.Id);
        Assert.True(await _queue.SuspendAsync(seed.Id));
        Assert.False(await _queue.SuspendAsync(idle.Id));
        _queue.Continue(seed.Id);
        await Until(() => seed.State == DownloadState.Seeding);
        Assert.Equal(DownloadState.Paused, idle.State);
    }

    [Fact]
    public void NextStart_SkipsDaysTheQueueDoesNotRun()
    {
        var q = new NamedQueue { Start = "09:00", Days = "0000011" };          // Saturday and Sunday
        var wednesday = new DateTime(2026, 10, 28, 12, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 31, 9, 0, 0), q.NextStart(wednesday));
        Assert.Null(new NamedQueue { Start = null }.NextStart(wednesday));
    }
}

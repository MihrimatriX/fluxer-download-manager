namespace Fluxer.Core.Downloads;

public enum DownloadKind { Http = 0, Torrent = 1, Media = 2 }

public enum DownloadState
{
    Queued = 0,
    Starting = 1,
    Downloading = 2,
    Paused = 3,
    NeedsLinkRefresh = 4,
    DiskFull = 5,
    Verifying = 6,
    Seeding = 7,
    Completed = 8,
    Failed = 9,
}

/// The single face that HTTP, torrent and media downloads show to the queue and the UI.
public interface IDownload
{
    DownloadRecord Record { get; }
    long Id { get; }
    DownloadKind Kind { get; }
    DownloadState State { get; }
    string FileName { get; }

    /// Does it hold a queue slot right now? (Seeding torrents do not.)
    bool OccupiesSlot => State is DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying;

    /// Runs until completed, failed or cancelled. Cancellation = pause (state is checkpointed).
    Task RunAsync(CancellationToken ct);

    /// Cheap, lock-light; the UI calls it at ~4 Hz for visible rows only.
    DownloadProgress GetProgress();

    /// Deletes data on disk (called after the download stopped).
    void DeleteFiles();

    /// Rare events only (state transitions). Raised on a thread-pool thread.
    event Action<IDownload>? StateChanged;
}

public readonly record struct DownloadProgress(
    long BytesDone,
    long? TotalBytes,
    long BytesPerSecond,
    TimeSpan? Eta,
    int Connections)
{
    public double Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesDone / TotalBytes.Value, 0, 1) : 0;
}

/// Headers captured from the browser; sent with every request of the download.
public sealed record RequestContext(string? Referrer, string? UserAgent, string? Cookies);

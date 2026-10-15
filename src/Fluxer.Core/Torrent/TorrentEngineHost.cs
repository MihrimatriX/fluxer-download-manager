using MonoTorrent.Client;

namespace Fluxer.Core.Torrent;

/// The single MonoTorrent engine. DHT, PEX, LSD, UPnP/NAT-PMP on; fast-resume and magnet metadata
/// are cached by MonoTorrent itself under `cacheDirectory`. Torrent rate limits live here
/// (MonoTorrent's built-in limiter), independent of the HTTP token buckets.
public sealed class TorrentEngineHost : IAsyncDisposable
{
    public TorrentEngineHost(string cacheDirectory, long downloadLimit, long uploadLimit)
    {
        Directory.CreateDirectory(cacheDirectory);
        Engine = new ClientEngine(Build(cacheDirectory, downloadLimit, uploadLimit));
    }

    public ClientEngine Engine { get; }

    public Task SetLimitsAsync(long downloadLimit, long uploadLimit) =>
        Engine.UpdateSettingsAsync(Build(Engine.Settings.CacheDirectory, downloadLimit, uploadLimit));

    private static EngineSettings Build(string cacheDirectory, long down, long up) => new EngineSettingsBuilder
    {
        CacheDirectory = cacheDirectory,
        AllowPortForwarding = true,
        AllowLocalPeerDiscovery = true,
        AutoSaveLoadDhtCache = true,
        AutoSaveLoadFastResume = true,
        AutoSaveLoadMagnetLinkMetadata = true,
        MaximumDownloadRate = (int)Math.Min(int.MaxValue, down),
        MaximumUploadRate = (int)Math.Min(int.MaxValue, up),
    }.ToSettings();

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAllAsync(TimeSpan.FromSeconds(5));
        Engine.Dispose();
    }
}

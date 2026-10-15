using System.Net;
using Fluxer.Core.Downloads;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace Fluxer.Core.Torrent;

/// The single MonoTorrent engine. DHT, PEX, LSD, UPnP/NAT-PMP follow the settings; fast-resume and magnet
/// metadata are cached by MonoTorrent itself under `cacheDirectory`. Torrent rate limits live here
/// (MonoTorrent's built-in limiter), independent of the HTTP token buckets.
public sealed class TorrentEngineHost : IAsyncDisposable
{
    public TorrentEngineHost(string cacheDirectory, AppSettings settings)
    {
        Directory.CreateDirectory(cacheDirectory);
        Engine = new ClientEngine(Build(cacheDirectory, settings));
    }

    public ClientEngine Engine { get; }

    public int DhtNodes => Engine.Dht.NodeCount;

    /// Limits, port and network switches, applied live.
    public Task ApplyAsync(AppSettings settings) => Engine.UpdateSettingsAsync(Build(Engine.Settings.CacheDirectory, settings));

    private static EngineSettings Build(string cacheDirectory, AppSettings s) => new EngineSettingsBuilder
    {
        CacheDirectory = cacheDirectory,
        AllowPortForwarding = s.PortForwarding,
        AllowLocalPeerDiscovery = s.LocalPeerDiscovery,
        AutoSaveLoadDhtCache = true,
        AutoSaveLoadFastResume = true,
        AutoSaveLoadMagnetLinkMetadata = true,
        ListenEndPoints = new()
        {
            ["ipv4"] = new IPEndPoint(IPAddress.Any, s.ListenPort),
            ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, s.ListenPort),
        },
        DhtEndPoint = s.Dht ? new IPEndPoint(IPAddress.Any, s.ListenPort) : null,
        AllowedEncryption = s.Encryption switch
        {
            "require" => [EncryptionType.RC4Full, EncryptionType.RC4Header],
            "off" => [EncryptionType.PlainText],
            _ => [EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText],
        },
        MaximumDownloadRate = (int)Math.Min(int.MaxValue, s.TorrentDownloadLimit),
        MaximumUploadRate = (int)Math.Min(int.MaxValue, s.TorrentUploadLimit),
    }.ToSettings();

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAllAsync(TimeSpan.FromSeconds(5));
        Engine.Dispose();
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Fluxer.Core.Downloads;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using Serilog;

namespace Fluxer.Core.Torrent;

/// The single MonoTorrent engine. DHT, PEX, LSD, UPnP/NAT-PMP follow the settings; fast-resume and magnet
/// metadata are cached by MonoTorrent itself under `cacheDirectory`. Torrent rate limits live here
/// (MonoTorrent's built-in limiter), independent of the HTTP token buckets.
public sealed class TorrentEngineHost : IAsyncDisposable
{
    public TorrentEngineHost(string cacheDirectory, AppSettings settings, PeerBlocker? blocker = null)
    {
        Directory.CreateDirectory(cacheDirectory);
        if (settings.Dht) SeedDhtCache(Path.Combine(cacheDirectory, "dht_nodes.cache"));
        Engine = new ClientEngine(Build(cacheDirectory, settings));
        if (blocker is not null) Engine.ConnectionManager.BanPeer += (_, e) => e.BanPeer = blocker.IsBlocked(e.Peer.ConnectionUri);
    }

    private static readonly (string Host, int Port)[] DhtRouters =
        [("router.bittorrent.com", 6881), ("dht.transmissionbt.com", 6881), ("router.utorrent.com", 6881), ("dht.libtorrent.org", 25401)];

    /// Works around two MonoTorrent 3.0.2 bugs that keep DHT stuck in "Initialising" forever:
    /// it saves dht_nodes.cache as a bencoded list but reads it back as raw 26-byte compact nodes
    /// (so every cached node decodes to a garbage address), and with no usable cache it bootstraps
    /// from router.bittorrent.com alone, which often does not answer. Before the engine reads the file,
    /// rewrite it in the raw form it reads and add more routers; their made-up node ids are replaced
    /// by the real ones once they answer.
    internal static void SeedDhtCache(string path)
    {
        var nodes = new List<byte[]>();
        try
        {
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
            if (bytes.Length > 0 && bytes[0] == (byte)'l' && BEncodedValue.Decode(bytes) is BEncodedList list)
                nodes.AddRange(list.OfType<BEncodedString>().Select(n => n.Span.ToArray()).Where(n => n.Length == 26));
            else if (bytes.Length % 26 == 0)
                nodes.AddRange(bytes.Chunk(26));
        }
        catch (Exception e) when (e is IOException or BEncodingException) { }

        var lookups = DhtRouters.Select(r => (r.Port, Task: Dns.GetHostAddressesAsync(r.Host, AddressFamily.InterNetwork))).ToList();
        try { Task.WaitAll([.. lookups.Select(l => l.Task)], TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { } // an unresolvable router is skipped below
        foreach (var (port, task) in lookups.Where(l => l.Task.IsCompletedSuccessfully))
            foreach (var ip in task.Result)
            {
                var node = new byte[26];
                Random.Shared.NextBytes(node.AsSpan(0, 20));
                ip.TryWriteBytes(node.AsSpan(20, 4), out _);
                BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(24), (ushort)port);
                nodes.Add(node);
            }
        // one entry per address: the routers are re-added on every start
        var raw = nodes.DistinctBy(n => Convert.ToHexString(n, 20, 6)).SelectMany(n => n).ToArray();
        try { File.WriteAllBytes(path, raw); }
        catch (IOException e) { Log.Warning(e, "Seeding the DHT cache"); }
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
        // MonoTorrent's defaults (8 half-open, 10 s timeout, 150 total, 5 MB cache) starve swarms: in a busy
        // public swarm >90 % of TCP attempts fail (NATed peers), each holding a slot until the timeout.
        // Measured on a 116-tracker game torrent: 64/10 s reached 15 open connections in 90 s and ~0 MB/s
        // after 30 s; 200/5 s reached 47 connections and 1.4 MB/s after 30 s.
        MaximumHalfOpenConnections = 200,
        ConnectionTimeout = TimeSpan.FromSeconds(5),
        MaximumConnections = Math.Max(150, s.MaxPeersPerTorrent * Math.Max(1, s.TorrentSlots)),
        DiskCacheBytes = 32 * 1024 * 1024,
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

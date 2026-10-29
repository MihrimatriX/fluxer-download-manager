using System.Net;

namespace Fluxer.Core.Torrent;

/// Who may not connect: the user's block list (IpFilter) and the peers they banned by hand. MonoTorrent asks before
/// every connection, incoming or outgoing (ConnectionManager.BanPeer); swapping the list never blocks that check.
public sealed class PeerBlocker
{
    private volatile IpFilter _filter = IpFilter.Empty;
    private volatile HashSet<IPAddress> _banned = [];
    private long _refused;

    public IpFilter Filter { get => _filter; set => _filter = value; }

    public void SetBanned(IEnumerable<string> addresses) =>
        _banned = [.. addresses.Select(a => IPAddress.TryParse(a, out var ip) ? Normal(ip) : null).OfType<IPAddress>()];

    /// Connections refused since the start.
    public long Refused => Interlocked.Read(ref _refused);

    /// MonoTorrent's peer addresses look like "ipv4://1.2.3.4:6881" or "ipv6://[::1]:6881".
    public bool IsBlocked(Uri peer)
    {
        if (!IPAddress.TryParse(peer.Host.Trim('[', ']'), out var ip)) return false;
        var blocked = _banned.Contains(Normal(ip)) || _filter.IsBlocked(ip);
        if (blocked) Interlocked.Increment(ref _refused);
        return blocked;
    }

    /// "::ffff:1.2.3.4" and "1.2.3.4" are the same peer.
    private static IPAddress Normal(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace Fluxer.Core.Torrent;

/// A block list of address ranges, as published for P2P clients: eMule/uTorrent "ipfilter.dat"
/// ("1.2.3.0 - 1.2.3.255 , 100 , name"; levels of 128 and above are allowed), PeerGuardian ".p2p"
/// ("name:1.2.3.0-1.2.3.255"), CIDR ("1.2.3.0/24") or single addresses; IPv4 and IPv6, plain or gzip.
/// Ranges are merged and sorted once, so a lookup is a binary search.
public sealed class IpFilter
{
    private readonly (UInt128 From, UInt128 To)[] _ranges;

    private IpFilter((UInt128 From, UInt128 To)[] ranges) => _ranges = ranges;

    public static readonly IpFilter Empty = new([]);

    public int RangeCount => _ranges.Length;

    public bool IsBlocked(IPAddress ip)
    {
        var key = Key(ip);
        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (key < _ranges[mid].From) hi = mid - 1;
            else if (key > _ranges[mid].To) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// IPv4 lives in the IPv4-mapped IPv6 space, so one table holds both.
    private static UInt128 Key(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork) ip = ip.MapToIPv6();
        Span<byte> b = stackalloc byte[16];
        ip.TryWriteBytes(b, out _);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(b);
    }

    public static IpFilter Load(string path)
    {
        using var file = File.OpenRead(path);
        return Read(file);
    }

    /// Plain text or gzip (the usual way lists are distributed).
    public static IpFilter Read(Stream stream)
    {
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }
        var start = stream.Position;
        var gzip = stream.ReadByte() == 0x1f && stream.ReadByte() == 0x8b;
        stream.Position = start;
        using var reader = new StreamReader(gzip ? new GZipStream(stream, CompressionMode.Decompress) : stream);
        return Parse(reader);
    }

    public static IpFilter Parse(TextReader reader)
    {
        var ranges = new List<(UInt128 From, UInt128 To)>();
        while (reader.ReadLine() is { } line)
            if (ParseLine(line) is { } r) ranges.Add(r);
        ranges.Sort((a, b) => a.From.CompareTo(b.From));
        var merged = new List<(UInt128 From, UInt128 To)>(ranges.Count);
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && (r.From <= merged[^1].To || r.From == merged[^1].To + 1))
                merged[^1] = (merged[^1].From, UInt128.Max(merged[^1].To, r.To));
            else merged.Add(r);
        }
        return new IpFilter([.. merged]);
    }

    internal static (UInt128 From, UInt128 To)? ParseLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] is '#' or ';' || line.StartsWith("//", StringComparison.Ordinal)) return null;

        // eMule: "from - to , level , description"
        var parts = line.Split(',');
        if (parts.Length >= 2 && Range(parts[0]) is { } emule)
            return int.TryParse(parts[1].Trim(), out var level) && level >= 128 ? null : emule;

        // PeerGuardian: "description:from-to"; the description may hold colons, and IPv6 addresses do too, so the
        // range starts at the first colon after which "from" reads as an address of the same kind as "to".
        var dash = line.LastIndexOf('-');
        if (dash > 0 && IPAddress.TryParse(line[(dash + 1)..].Trim(), out var end))
        {
            for (var colon = line.IndexOf(':'); colon >= 0 && colon < dash; colon = line.IndexOf(':', colon + 1))
                if (IPAddress.TryParse(line[(colon + 1)..dash].Trim(), out var begin) && begin.AddressFamily == end.AddressFamily)
                    return Range(line[(colon + 1)..]);
            return Range(line);
        }
        // CIDR or a single address
        var slash = line.IndexOf('/');
        if (slash > 0 && IPAddress.TryParse(line[..slash], out var net) && int.TryParse(line[(slash + 1)..], out var bits))
        {
            var key = Key(net);
            var width = net.AddressFamily == AddressFamily.InterNetwork ? bits + 96 : bits;
            if (width is < 0 or > 128) return null;
            var mask = width == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - width);
            return (key & mask, (key & mask) | ~mask);
        }
        return IPAddress.TryParse(line, out var single) ? (Key(single), Key(single)) : null;
    }

    private static (UInt128 From, UInt128 To)? Range(string text)
    {
        var dash = text.IndexOf('-');
        if (dash <= 0) return null;
        // eMule lists pad with zeros ("001.002.003.004"), which IPAddress reads as octal-free decimal anyway.
        if (!IPAddress.TryParse(Unpad(text[..dash].Trim()), out var from) || !IPAddress.TryParse(Unpad(text[(dash + 1)..].Trim()), out var to)) return null;
        if (from.AddressFamily != to.AddressFamily) return null;
        var (a, b) = (Key(from), Key(to));
        return a <= b ? (a, b) : (b, a);
    }

    /// "001.002.003.004" → "1.2.3.4" (a leading zero would otherwise be refused or read as octal).
    private static string Unpad(string ip) =>
        ip.Contains(':') ? ip : string.Join('.', ip.Split('.').Select(p => p.TrimStart('0') is { Length: > 0 } t ? t : "0"));
}

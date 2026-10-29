using System.IO.Compression;
using System.Net;
using System.Text;
using Fluxer.Core.Torrent;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class TorrentExtrasTests
{
    private static IpFilter Filter(string text) => IpFilter.Parse(new StringReader(text));

    [Fact]
    public void IpFilter_ReadsTheCommonFormats()
    {
        var f = Filter("""
            # comment
            001.002.003.000 - 001.002.003.255 , 000 , eMule range
            005.000.000.000 - 005.255.255.255 , 200 , allowed (level 128 and up)
            Some-Org, Inc:10.0.0.0-10.0.0.15
            v6 net:2001:db8::-2001:db8::ffff
            192.168.7.0/24
            8.8.8.8
            not a line
            """);
        Assert.True(f.IsBlocked(IPAddress.Parse("1.2.3.200")));
        Assert.False(f.IsBlocked(IPAddress.Parse("1.2.4.0")));
        Assert.False(f.IsBlocked(IPAddress.Parse("5.5.5.5")));
        Assert.True(f.IsBlocked(IPAddress.Parse("10.0.0.15")));
        Assert.False(f.IsBlocked(IPAddress.Parse("10.0.0.16")));
        Assert.True(f.IsBlocked(IPAddress.Parse("2001:db8::abcd")));
        Assert.False(f.IsBlocked(IPAddress.Parse("2001:db8::1:0")));
        Assert.True(f.IsBlocked(IPAddress.Parse("192.168.7.99")));
        Assert.True(f.IsBlocked(IPAddress.Parse("8.8.8.8")));
        Assert.True(f.IsBlocked(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.False(f.IsBlocked(IPAddress.Parse("8.8.4.4")));
    }

    [Fact]
    public void IpFilter_MergesOverlaps_AndReadsGzip()
    {
        var text = "1.0.0.0-1.0.0.10\n1.0.0.5-1.0.0.20\n1.0.0.21-1.0.0.30\n9.9.9.9\n";
        Assert.Equal(2, Filter(text).RangeCount);

        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) z.Write(Encoding.ASCII.GetBytes(text));
        gz.Position = 0;
        var f = IpFilter.Read(gz);
        Assert.True(f.IsBlocked(IPAddress.Parse("1.0.0.25")));
        Assert.Equal(2, f.RangeCount);
    }

    [Fact]
    public void PeerBlocker_ChecksManualBansAndTheList()
    {
        var b = new PeerBlocker { Filter = Filter("6.6.6.0/24") };
        b.SetBanned(["1.2.3.4", "2001:db8::7", "garbage"]);
        Assert.True(b.IsBlocked(new Uri("ipv4://1.2.3.4:6881")));
        Assert.True(b.IsBlocked(new Uri("ipv6://[2001:db8::7]:51413")));
        Assert.True(b.IsBlocked(new Uri("ipv4://6.6.6.66:1")));
        Assert.False(b.IsBlocked(new Uri("ipv4://1.2.3.5:6881")));
        Assert.Equal(3, b.Refused);
    }

    [Fact]
    public void TrackerList_KeepsAnnounceAddressesOnce()
    {
        var list = TrackerList.Parse("udp://tracker.example.org:1337/announce\n\nhttps://t.example.net/announce\nUDP://tracker.example.org:1337/announce\nfile:///etc/passwd\nnonsense\nwss://w.example/announce");
        Assert.Equal(["udp://tracker.example.org:1337/announce", "https://t.example.net/announce", "wss://w.example/announce"], list);
        Assert.Equal(["udp://a.example:1/announce", "udp://b.example:1/announce"],
            TrackerList.Merge(["udp://a.example:1/announce"], ["udp://a.example:1/announce", "udp://b.example:1/announce"]));
    }

    [Fact]
    public void Torznab_ParsesResultsAndErrors()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel>
                <item>
                  <title>Ubuntu 24.04 Desktop</title>
                  <guid>https://tracker.example/details/1</guid>
                  <jackettindexer id="x">Example</jackettindexer>
                  <pubDate>Tue, 28 Oct 2026 10:00:00 +0000</pubDate>
                  <size>6000000000</size>
                  <link>https://jackett.local/dl/1.torrent</link>
                  <enclosure url="https://jackett.local/dl/1.torrent" length="6000000000" type="application/x-bittorrent" />
                  <torznab:attr name="seeders" value="120" />
                  <torznab:attr name="peers" value="150" />
                  <torznab:attr name="magneturl" value="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567" />
                </item>
                <item>
                  <title>Only a magnet</title>
                  <link>magnet:?xt=urn:btih:89abcdef0123456789abcdef0123456789abcdef</link>
                  <torznab:attr name="size" value="1024" />
                </item>
              </channel>
            </rss>
            """;
        var results = Torznab.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "Jackett");
        Assert.Equal(2, results.Count);
        var a = results[0];
        Assert.Equal(("Ubuntu 24.04 Desktop", 6000000000L, 120, 30, "Example"), (a.Title, a.Size!.Value, a.Seeders!.Value, a.Leechers!.Value, a.Indexer));
        Assert.StartsWith("magnet:", a.Target);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero), a.Published);
        var b = results[1];
        Assert.Equal(("Only a magnet", 1024L, (string?)null, "Jackett"), (b.Title, b.Size!.Value, b.Link, b.Indexer));
        Assert.StartsWith("magnet:", b.Target);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Torznab.Parse(new MemoryStream("<error code=\"100\" description=\"Invalid API Key\"/>"u8.ToArray()), "x"));
        Assert.Equal("Invalid API Key", error.Message);
        Assert.Throws<System.Xml.XmlException>(() =>
            Torznab.Parse(new MemoryStream("<!DOCTYPE x [<!ENTITY a \"b\">]><rss>&a;</rss>"u8.ToArray()), "x"));
    }

    [Fact]
    public void Torznab_SearchUri_CarriesQueryAndKey()
    {
        var uri = Torznab.SearchUri(new TorznabIndexer { Url = "http://127.0.0.1:9117/api/v2.0/indexers/all/results/torznab/api?x=1" }, "ubuntu iso & more", "4000");
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(("search", "ubuntu iso & more", "4000", "1"), (q["t"], q["q"], q["cat"], q["x"]));
    }
}

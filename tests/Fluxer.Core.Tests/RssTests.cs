using System.Text;
using Fluxer.Core.Torrent;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class RssParserTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Show";

    [Fact]
    public void Rss2_EnclosureMagnetAndTorrentLink()
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE rss PUBLIC "-//Netscape Communications//DTD RSS 0.91//EN" "http://my.netscape.com/publish/formats/rss-0.91.dtd">
            <rss version="2.0" xmlns:torrent="http://xmlns.ezrss.it/0.1/">
              <channel>
                <title>Tracker</title>
                <item>
                  <title>Show S01E02 1080p</title>
                  <guid isPermaLink="false">id-2</guid>
                  <link>https://tracker.example/details/2</link>
                  <enclosure url="https://tracker.example/dl/2?passkey=x" type="application/x-bittorrent" length="12345" />
                </item>
                <item>
                  <title>Show S01E01 720p</title>
                  <link>{Magnet.Replace("&", "&amp;")}</link>
                </item>
                <item>
                  <title>Other</title>
                  <link>https://site.example/files/other.torrent</link>
                  <torrent:magnetURI>{Magnet.Replace("&", "&amp;")}</torrent:magnetURI>
                </item>
                <item><title>Just a news post</title><link>https://site.example/news</link></item>
              </channel>
            </rss>
            """;
        var items = RssParser.Parse(xml);

        Assert.Equal(3, items.Count);
        Assert.Equal(new RssItem("id-2", "Show S01E02 1080p", "https://tracker.example/dl/2?passkey=x", null), items[0]);
        Assert.Equal(new RssItem(Magnet, "Show S01E01 720p", null, Magnet), items[1]);
        Assert.Equal(new RssItem("https://site.example/files/other.torrent", "Other", "https://site.example/files/other.torrent", Magnet), items[2]);
    }

    [Fact]
    public void Atom_LinkHrefAndEnclosureRel()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom feed</title>
              <entry>
                <title>Linux ISO</title>
                <id>urn:uuid:1</id>
                <link rel="alternate" href="https://site.example/post/1" />
                <link rel="enclosure" type="application/x-bittorrent" href="https://site.example/get/1" />
              </entry>
              <entry>
                <title>Magnet only</title>
                <id>urn:uuid:2</id>
                <link href="magnet:?xt=urn:btih:ffffffffffffffffffffffffffffffffffffffff" />
              </entry>
              <entry>
                <title>Nothing to download</title>
                <id>urn:uuid:3</id>
                <link href="https://site.example/post/3" />
              </entry>
            </feed>
            """;
        var items = RssParser.Parse(xml);

        Assert.Equal(2, items.Count);
        Assert.Equal(new RssItem("urn:uuid:1", "Linux ISO", "https://site.example/get/1", null), items[0]);
        Assert.Equal("urn:uuid:2", items[1].Id);
        Assert.Equal("magnet:?xt=urn:btih:ffffffffffffffffffffffffffffffffffffffff", items[1].Magnet);
        Assert.Null(items[1].TorrentUrl);
    }

    [Fact]
    public void Stream_HonoursDeclaredEncoding_AndIgnoresNonHttpLinks()
    {
        var xml = """
            <?xml version="1.0" encoding="ISO-8859-9"?>
            <rss version="2.0"><channel>
              <item><title>Dizi Bölüm 1</title><link>https://s.example/a.torrent</link></item>
              <item><title>Local</title><link>file:///C:/x.torrent</link></item>
            </channel></rss>
            """;
        var bytes = Encoding.Latin1.GetBytes(xml); // ö is the same byte in 8859-9
        var items = RssParser.Parse(new MemoryStream(bytes));

        var item = Assert.Single(items);
        Assert.Equal("Dizi Bölüm 1", item.Title);
    }

    [Fact]
    public void Malformed_Throws() => Assert.ThrowsAny<System.Xml.XmlException>(() => RssParser.Parse("<rss><channel>"));
}

public sealed class RssFilterTests
{
    private static RssItem Item(string id, string title = "") => new(id, title, "https://t.example/" + id + ".torrent", null);

    [Theory]
    [InlineData(null, null, "Anything", true)]
    [InlineData("1080p", null, "Show S01E01 1080P WEB", true)]          // case-insensitive
    [InlineData("1080p", null, "Show S01E01 720p", false)]
    [InlineData(null, "cam|ts", "Movie 2026 CAM", false)]
    [InlineData("show", "s01e0[12]", "Show S01E03", true)]
    [InlineData("show", "s01e0[12]", "Show S01E02", false)]
    [InlineData("  ", "", "Blank filters mean none", true)]
    [InlineData("(unclosed", null, "(unclosed", false)]                  // invalid include → nothing
    [InlineData(null, "(unclosed", "Anything", false)]                    // invalid exclude → nothing
    public void Matches(string? include, string? exclude, string title, bool expected) =>
        Assert.Equal(expected, RssFilter.Matches(new RssFeed { Include = include, Exclude = exclude }, title));

    [Fact]
    public void CatastrophicPattern_TimesOutAsNoMatch()
    {
        var feed = new RssFeed { Include = "^(a+)+$" };
        Assert.False(RssFilter.Matches(feed, new string('a', 40) + "!"));
    }

    [Fact]
    public void IsValid()
    {
        Assert.True(RssFilter.IsValid(null));
        Assert.True(RssFilter.IsValid("x264|x265"));
        Assert.False(RssFilter.IsValid("[a-"));
    }

    [Fact]
    public void FirstFetch_MarksEverythingSeen_AddsNothing()
    {
        var state = new RssFeedState();
        var first = RssFilter.TakeNew(new RssFeed(), state, [Item("3"), Item("2"), Item("1")]);

        Assert.Empty(first);
        Assert.True(state.Primed);
        Assert.Equal(["1", "2", "3"], state.Seen);   // oldest first
    }

    [Fact]
    public void LaterFetch_ReturnsOnlyNewMatching_DedupedWithinBatch()
    {
        var feed = new RssFeed { Include = "1080p" };
        var state = new RssFeedState();
        RssFilter.TakeNew(feed, state, [Item("1", "A 1080p")]);

        var fresh = RssFilter.TakeNew(feed, state, [Item("3", "C 1080p"), Item("3", "C 1080p"), Item("2", "B 720p"), Item("1", "A 1080p")]);

        Assert.Equal(["3"], fresh.Select(i => i.Id));
        // Not marked until the poller added it: a failed .torrent fetch is retried next round.
        Assert.DoesNotContain("3", state.Seen);
        state.MarkSeen("3");
        Assert.Empty(RssFilter.TakeNew(feed, state, [Item("3", "C 1080p")]));
    }

    [Fact]
    public void Seen_IsBounded_KeepsNewest()
    {
        var state = new RssFeedState();
        for (var i = 0; i < RssFeedState.MaxSeen + 50; i++) state.MarkSeen(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        state.MarkSeen("10");  // trimmed earlier → added again at the end

        Assert.Equal(RssFeedState.MaxSeen, state.Seen.Count);
        Assert.Equal("10", state.Seen[^1]);
        Assert.DoesNotContain("0", state.Seen);
        Assert.Contains("1049", state.Seen);
    }

    [Fact]
    public void FirstFetch_LongerThanBound_KeepsNewestItems()
    {
        var state = new RssFeedState();
        var items = Enumerable.Range(0, RssFeedState.MaxSeen + 10).Reverse().Select(i => Item(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        RssFilter.TakeNew(new RssFeed(), state, items);

        Assert.Equal(RssFeedState.MaxSeen, state.Seen.Count);
        Assert.Contains((RssFeedState.MaxSeen + 9).ToString(System.Globalization.CultureInfo.InvariantCulture), state.Seen);
        Assert.DoesNotContain("0", state.Seen);
    }
}

/// The poller against a canned feed: first round primes, a later item is added once, a broken feed
/// does not stop the next one.
public sealed class RssPollerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fluxer-rss-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _responses = [];

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Feed(params int[] ids) =>
        "<rss version=\"2.0\"><channel>" + string.Concat(ids.Select(i =>
            $"<item><title>Item {i}</title><guid>g{i}</guid><link>{Magnet(i)}</link></item>")) + "</channel></rss>";

    private static string Magnet(int i) => "magnet:?xt=urn:btih:" + i.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task PrimesThenAddsOnlyNewItems()
    {
        Directory.CreateDirectory(_dir);
        using var db = new Storage.Database(Path.Combine(_dir, "t.db"));
        var settings = new Downloads.AppSettings
        {
            RssFeeds = [new() { Url = "https://bad.example/rss" }, new() { Url = "https://good.example/rss" }],
        };
        var added = new List<string>();
        using var http = new HttpClient(new Canned(_responses));
        await using var poller = new RssPoller(() => http, () => settings, db, (_, magnet, _) => { lock (added) added.Add(magnet!); return null; });
        using var rounds = new SemaphoreSlim(0);
        poller.Checked += () => rounds.Release();
        poller.Start();

        lock (_responses) _responses["https://good.example/rss"] = Feed(2, 1);
        poller.CheckNow();
        Assert.True(await rounds.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(added);
        Assert.NotNull(poller.StateOf("https://bad.example/rss")!.LastError);
        Assert.True(poller.StateOf("https://good.example/rss")!.Primed);

        lock (_responses) _responses["https://good.example/rss"] = Feed(3, 2, 1);
        poller.CheckNow();
        Assert.True(await rounds.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal([Magnet(3)], added);

        poller.CheckNow();
        Assert.True(await rounds.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(added);
        Assert.Equal(0, poller.StateOf("https://good.example/rss")!.LastAdded);
    }

    private sealed class Canned(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body;
            lock (responses) responses.TryGetValue(request.RequestUri!.ToString(), out body);
            return Task.FromResult(body is not null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/rss+xml") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}

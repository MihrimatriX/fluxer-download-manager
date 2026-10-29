using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Fluxer.Core.Platform.Windows;

namespace Fluxer.Core.Torrent;

/// A Torznab indexer (Jackett, Prowlarr or a tracker's own endpoint). The API key is stored DPAPI-encrypted.
public sealed class TorznabIndexer
{
    public string Name { get; set; } = "";
    /// "http://127.0.0.1:9117/api/v2.0/indexers/all/results/torznab" (Jackett), "http://127.0.0.1:9696/1/api" (Prowlarr).
    public string Url { get; set; } = "";
    public string? ApiKeyProtected { get; set; }

    [JsonIgnore]
    public string? ApiKey
    {
        get => Dpapi.UnprotectBase64(ApiKeyProtected);
        set => ApiKeyProtected = Dpapi.ProtectToBase64(value);
    }
}

public sealed record TorznabResult(string Title, long? Size, int? Seeders, int? Leechers, string? Magnet, string? Link,
    DateTimeOffset? Published, string Indexer, string? Details)
{
    /// What to hand to the torrent flow: the magnet when there is one, else the .torrent address.
    public string? Target => Magnet ?? Link;
}

public static class Torznab
{
    private static readonly XNamespace Ns = "http://torznab.com/schemas/2015/feed";

    /// "q" search, at most 100 results; categories as Torznab numbers (2000 movies, 5000 TV, 3000 audio, 4000 PC).
    public static Uri SearchUri(TorznabIndexer indexer, string query, string? categories = null)
    {
        var b = new UriBuilder(indexer.Url.Trim());
        var q = System.Web.HttpUtility.ParseQueryString(b.Query);
        q["t"] = "search";
        q["q"] = query;
        q["limit"] = "100";
        if (categories is { Length: > 0 }) q["cat"] = categories;
        if (indexer.ApiKey is { Length: > 0 } key) q["apikey"] = key;
        b.Query = q.ToString();
        return b.Uri;
    }

    public static async Task<List<TorznabResult>> SearchAsync(HttpClient http, TorznabIndexer indexer, string query, string? categories, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var res = await http.GetAsync(SearchUri(indexer, query, categories), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        return Parse(stream, indexer.Name);
    }

    /// RFC 822 dates ("Wed, 28 Oct 2026 10:00:00 +0000"); the weekday is dropped, as some indexers get it wrong.
    private static DateTimeOffset? Date(string? s)
    {
        if (s is null) return null;
        var comma = s.IndexOf(',');
        return DateTimeOffset.TryParse(comma >= 0 ? s[(comma + 1)..] : s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;
    }

    /// RSS with torznab:attr elements; an <error code=… description=…/> reply throws with its description.
    public static List<TorznabResult> Parse(Stream xml, string indexer)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32_000_000 };
        using var reader = XmlReader.Create(xml, settings);
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName == "error")
            throw new InvalidOperationException((string?)doc.Root.Attribute("description") ?? (string?)doc.Root.Attribute("code") ?? "error");
        var list = new List<TorznabResult>();
        foreach (var item in doc.Descendants("item"))
        {
            var attrs = item.Elements(Ns + "attr")
                .GroupBy(a => (string?)a.Attribute("name") ?? "")
                .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("value"), StringComparer.OrdinalIgnoreCase);
            string? Attr(string name) => attrs.GetValueOrDefault(name);
            static long? Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

            var link = (string?)item.Element("enclosure")?.Attribute("url") ?? (string?)item.Element("link");
            var magnet = Attr("magneturl") ?? (link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true ? link : null);
            if (magnet is not null && link == magnet) link = null;
            var seeders = Long(Attr("seeders"));
            var peers = Long(Attr("peers"));
            list.Add(new TorznabResult(
                Title: ((string?)item.Element("title"))?.Trim() ?? "?",
                Size: Long(Attr("size")) ?? Long((string?)item.Element("size")) ?? Long((string?)item.Element("enclosure")?.Attribute("length")),
                Seeders: (int?)seeders,
                Leechers: peers is { } p && seeders is { } s ? (int)Math.Max(0, p - s) : (int?)Long(Attr("leechers")),
                Magnet: magnet,
                Link: link is { } l && (l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? l : null,
                Published: Date((string?)item.Element("pubDate")),
                Indexer: (string?)item.Element("jackettindexer") ?? Attr("indexer") ?? indexer,
                Details: (string?)item.Element("comments") ?? (string?)item.Element("guid")));
        }
        return list;
    }
}

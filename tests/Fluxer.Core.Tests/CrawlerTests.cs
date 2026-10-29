using System.Net;
using System.Text;
using Fluxer.Core.Http;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class CrawlerTests
{
    /// Answers from a dictionary of pages; anything else is a 1 MB zip (whose body the crawler must not read).
    private sealed class Site(Dictionary<string, string> pages) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requested) Requested.Add(url);
            if (url.Contains("broken", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var res = pages.TryGetValue(url, out var html)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024 * 1024]) { Headers = { ContentType = new("application/zip") } } };
            return Task.FromResult(res);
        }
    }

    private static readonly Dictionary<string, string> Pages = new()
    {
        ["https://example.org/"] = """
            <base href="https://example.org/files/">
            <a href="a.zip">A</a> <a href='b.PDF#page=2'>B</a> <img src="/img/logo.png" srcset="/img/logo@2x.png 2x, /img/logo@3x.png 3x">
            <a href="../list.html">more</a> <a href="https://other.example.net/c.zip">other</a> <a href="https://other.example.net/page.html">other page</a>
            <a href="javascript:void(0)">x</a> <a href="mailto:a@b.c">m</a> <link href="/style.css"> <a href="/broken.html">broken</a>
            <a href="/get?id=7">behind a page link</a> <a href="a.zip">same again</a> <a href="/a?x=1&amp;y=2">entities</a>
            """,
        ["https://example.org/list.html"] = """<a href="/deep/d.mkv">D</a><a href="/deeper.html">deeper</a>""",
        ["https://example.org/deeper.html"] = """<a href="/e.iso">E</a>""",
        ["https://example.org/a?x=1&y=2"] = "<p>nothing</p>",
        ["https://other.example.net/page.html"] = """<a href="/f.zip">F</a>""",
    };

    private static async Task<(SiteCrawler.Result Result, Site Site)> Crawl(SiteCrawler.Options o)
    {
        var site = new Site(Pages);
        using var http = new HttpClient(site);
        var r = await SiteCrawler.CrawlAsync(http, new Uri("https://example.org/"), o, null, null, TestContext.Current.CancellationToken);
        return (r, site);
    }

    [Fact]
    public async Task OneLevel_SameSite()
    {
        var (r, site) = await Crawl(new SiteCrawler.Options(Depth: 1));
        var urls = r.Files.Select(f => f.Url).ToList();
        Assert.Contains("https://example.org/files/a.zip", urls);
        Assert.Contains("https://example.org/files/b.PDF", urls);
        Assert.Contains("https://example.org/img/logo@3x.png", urls);
        Assert.Contains("https://other.example.net/c.zip", urls);            // a file link elsewhere is still a file of this page
        Assert.Contains("https://example.org/deep/d.mkv", urls);             // found on the second level
        Assert.Contains("https://example.org/get?id=7", urls);               // page-like link that answered with a file
        Assert.DoesNotContain("https://example.org/e.iso", urls);            // one level too deep
        Assert.DoesNotContain("https://other.example.net/f.zip", urls);      // that page is on another site
        Assert.DoesNotContain(urls, u => u.EndsWith(".css", StringComparison.Ordinal));
        Assert.Single(urls, u => u.EndsWith("a.zip", StringComparison.Ordinal));
        Assert.Equal("image", r.Files.Single(f => f.Url.EndsWith("logo.png", StringComparison.Ordinal)).Kind);
        Assert.Equal("media", r.Files.Single(f => f.Url.EndsWith("d.mkv", StringComparison.Ordinal)).Kind);
        Assert.Equal(1, r.Failed);
        Assert.DoesNotContain(site.Requested, u => u.EndsWith(".zip", StringComparison.Ordinal));   // files are listed, not fetched
    }

    [Fact]
    public async Task ExtensionFilter_DepthZero_AndOtherSites()
    {
        var (only, _) = await Crawl(new SiteCrawler.Options(Depth: 0, Extensions: [".zip"]));
        Assert.Equal(["https://example.org/files/a.zip", "https://other.example.net/c.zip"], only.Files.Select(f => f.Url).Order());
        Assert.Equal(1, only.Pages);

        var (wide, _) = await Crawl(new SiteCrawler.Options(Depth: 1, SameSite: false));
        Assert.Contains("https://other.example.net/f.zip", wide.Files.Select(f => f.Url));
    }

    [Fact]
    public async Task MaxPages_IsRespected()
    {
        var (r, _) = await Crawl(new SiteCrawler.Options(Depth: 5, MaxPages: 2));
        Assert.True(r.Pages <= 2);
    }
}

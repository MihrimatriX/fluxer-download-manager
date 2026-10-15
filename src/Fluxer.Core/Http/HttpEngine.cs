using System.Net;

namespace Fluxer.Core.Http;

/// Shared HTTP stack: one SocketsHttpHandler for all downloads (connection pool), and the global
/// HTTP rate limit. Torrent limits live in MonoTorrent, not here.
public sealed class HttpEngine : IDisposable
{
    public const string DefaultUserAgent = "Fluxer/0.1";

    private HttpClient _client;

    public HttpEngine(string? proxy = null) => _client = Build(proxy);

    public HttpClient Client => _client;
    public TokenBucket GlobalBucket { get; } = new();

    /// Proxy changes need a new handler; in-flight requests keep the old one until they finish.
    public void SetProxy(string? proxy) => _client = Build(proxy);

    private static HttpClient Build(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            // Range offsets refer to the encoded entity; decompression would corrupt them.
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,                 // cookies are per download, sent as a header
            AllowAutoRedirect = true,           // final URL read from response.RequestMessage
            MaxAutomaticRedirections = 10,
            MaxConnectionsPerServer = 256,      // per-download limits are ours
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = false,
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static HttpRequestMessage NewRequest(HttpMethod method, string url, Downloads.RequestContext? ctx)
    {
        var req = new HttpRequestMessage(method, url)
        {
            // HTTP/2 multiplexes onto one TCP connection, which defeats multi-connection downloading.
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        req.Headers.TryAddWithoutValidation("User-Agent", ctx?.UserAgent ?? DefaultUserAgent);
        if (!string.IsNullOrEmpty(ctx?.Referrer)) req.Headers.TryAddWithoutValidation("Referer", ctx.Referrer);
        if (!string.IsNullOrEmpty(ctx?.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", ctx.Cookies);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        return req;
    }

    public void Dispose() => _client.Dispose();
}

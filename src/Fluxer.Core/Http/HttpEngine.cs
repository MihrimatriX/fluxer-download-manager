using System.Net;
using System.Net.Sockets;

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
            ConnectCallback = HappyEyeballsAsync,
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// RFC 8305-style connect: the default handler tries addresses one after another, so a host
    /// with a broken IPv6 route stalls every request until the OS gives up (~21 s). Here IPv6 and
    /// IPv4 attempts are interleaved and staggered by 250 ms; the first socket to connect wins.
    internal static async ValueTask<Stream> HappyEyeballsAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var (host, port) = (ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port);
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
        var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        var ordered = new List<IPAddress>();
        for (var i = 0; i < Math.Max(v6.Count, v4.Count); i++)
        {
            if (i < v6.Count) ordered.Add(v6[i]);
            if (i < v4.Count) ordered.Add(v4[i]);
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>();
        Exception? last = null;
        var next = 0;
        while (next < ordered.Count || pending.Count > 0)
        {
            if (next < ordered.Count) pending.Add(ConnectOneAsync(ordered[next++], port, race.Token));
            var stagger = next < ordered.Count ? Task.Delay(250, race.Token) : null;
            var done = await Task.WhenAny(stagger is null ? [.. pending] : [.. pending, stagger]);
            if (done == stagger) continue;

            var attempt = (Task<Socket>)done;
            pending.Remove(attempt);
            if (attempt.IsCompletedSuccessfully)
            {
                await race.CancelAsync();
                foreach (var loser in pending)
                    _ = loser.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                return new NetworkStream(attempt.Result, ownsSocket: true);
            }
            last = attempt.Exception?.InnerException ?? last;
        }
        ct.ThrowIfCancellationRequested();
        throw last ?? new SocketException((int)SocketError.HostNotFound);

        static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken ct)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, port, ct);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
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

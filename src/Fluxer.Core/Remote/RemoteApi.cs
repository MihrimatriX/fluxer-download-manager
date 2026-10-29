using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Torrent;
using Serilog;
using static Fluxer.Core.Remote.MiniHttpServer;

namespace Fluxer.Core.Remote;

/// Remote control of Fluxer: a small REST API under /api/v1, aria2's JSON-RPC at /jsonrpc (so aria2 front-ends and
/// "send to aria2" extensions work unchanged), and a web page at / that uses the REST API. Every call needs the API
/// key (Bearer header, or aria2's "token:" parameter); the page itself holds no data. It listens on 127.0.0.1 only,
/// unless the user opens it to the network, and refuses other Host names so a web page cannot reach it through DNS
/// rebinding.
public sealed class RemoteApi : IAsyncDisposable
{
    private readonly FluxerEngine _engine;
    private readonly byte[] _key;
    private readonly bool _lan;
    private readonly string _version;
    private readonly Aria2Rpc _aria2;
    private readonly Dictionary<IPAddress, (int Count, DateTime Since)> _failures = [];
    private MiniHttpServer? _server;

    public RemoteApi(FluxerEngine engine, string key, bool lan, string version)
    {
        _engine = engine;
        _key = Encoding.UTF8.GetBytes(key);
        _lan = lan;
        _version = version;
        _aria2 = new Aria2Rpc(engine, TokenOk, version);
    }

    public int Port => _server?.Port ?? 0;

    /// Throws SocketException when the port is taken.
    public void Start(int port)
    {
        _server = new MiniHttpServer(_lan ? IPAddress.Any : IPAddress.Loopback, port, HandleAsync);
        _server.Start();
    }

    /// 32 random bytes, URL-safe.
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private bool KeyMatches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _key);

    private bool TokenOk(string? token) => token is not null && token.StartsWith("token:", StringComparison.Ordinal) && KeyMatches(token[6..]);

    /// Ten wrong keys in a minute from one address block it for the rest of that minute.
    private bool Blocked(IPAddress ip)
    {
        lock (_failures)
            return _failures.TryGetValue(ip, out var f) && f.Count >= 10 && DateTime.UtcNow - f.Since < TimeSpan.FromMinutes(1);
    }

    private void Failed(IPAddress ip)
    {
        lock (_failures)
        {
            var now = DateTime.UtcNow;
            _failures[ip] = _failures.TryGetValue(ip, out var f) && now - f.Since < TimeSpan.FromMinutes(1) ? (f.Count + 1, f.Since) : (1, now);
        }
    }

    private static bool LocalHost(string? host)
    {
        if (host is null) return false;
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name is "127.0.0.1" or "localhost" or "[::1]";
    }

    internal async Task<Response> HandleAsync(Request req)
    {
        if (!_lan && !LocalHost(req.Header("Host"))) return Json(421, Error("host"));
        try
        {
            if (req.Path is "/" or "/index.html")
                return req.Method == "GET" ? new Response(200, "text/html; charset=utf-8", WebUi.Value, WebUiHeaders) : Json(405, Error("method"));
            if (req.Path == "/jsonrpc") return await Aria2Async(req);
            if (!req.Path.StartsWith("/api/", StringComparison.Ordinal)) return Json(404, Error("not-found"));
            if (Blocked(req.Remote)) return Json(429, Error("too-many-attempts"));
            var auth = req.Header("Authorization");
            var key = auth?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? auth[7..].Trim() : req.Header("X-Api-Key");
            if (!KeyMatches(key))
            {
                Failed(req.Remote);
                return Json(401, Error("unauthorized"));
            }
            return await RestAsync(req);
        }
        catch (JsonException) { return Json(400, Error("bad-json")); }
        catch (Exception e)
        {
            Log.Warning(e, "Remote {Method} {Path} failed", req.Method, req.Path);
            return Json(500, Error("internal"));
        }
    }

    // ================= aria2 =================

    /// Browser front-ends for aria2 run on other origins; the token in the body is the protection, as in aria2
    /// with --rpc-allow-origin-all.
    private static readonly Dictionary<string, string> Cors = new()
    {
        ["Access-Control-Allow-Origin"] = "*",
        ["Access-Control-Allow-Methods"] = "POST, OPTIONS",
        ["Access-Control-Allow-Headers"] = "Content-Type",
        ["Access-Control-Max-Age"] = "600",
    };

    private async Task<Response> Aria2Async(Request req)
    {
        if (req.Method == "OPTIONS") return new Response(204, "text/plain", [], Cors);
        if (req.Method != "POST") return Json(405, Error("method"));
        if (Blocked(req.Remote)) return Json(429, Error("too-many-attempts"));
        JsonNode? body;
        try { body = JsonNode.Parse(req.Body); }
        catch (JsonException) { return Aria2Json(Aria2Rpc.ErrorReply(null, -32700, "Parse error")); }
        var (reply, unauthorized) = await _aria2.HandleAsync(body);
        if (unauthorized) Failed(req.Remote);
        return Aria2Json(reply);
    }

    private static Response Aria2Json(JsonNode? reply) =>
        new(200, "application/json-rpc", Encoding.UTF8.GetBytes(reply?.ToJsonString() ?? "null"), Cors);

    // ================= REST =================

    private async Task<Response> RestAsync(Request req)
    {
        var parts = req.Path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);   // api, v1, ...
        if (parts.Length < 3 || parts[1] != "v1") return Json(404, Error("not-found"));
        var queue = _engine.Queue;
        switch (req.Method, parts[2], parts.Length)
        {
            case ("GET", "version", 3):
                return Json(200, new JsonObject { ["app"] = "Fluxer", ["version"] = _version });
            case ("GET", "stats", 3):
                return Json(200, Stats());
            case ("GET", "downloads", 3):
                return Json(200, new JsonObject { ["downloads"] = new JsonArray([.. queue.Items.Select(d => (JsonNode)Describe(d))]) });
            case ("POST", "downloads", 3):
                return Add(JsonNode.Parse(req.Body)?.AsObject());
            case ("POST", "pause-all", 3):
                await queue.PauseAllAsync();
                return Json(200, Ok());
            case ("POST", "resume-all", 3):
                queue.ResumeAll();
                return Json(200, Ok());
        }
        if (parts.Length is < 4 or > 5 || parts[2] != "downloads" || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || queue.Find(id) is not { } d)
            return Json(404, Error("not-found"));
        switch (req.Method, parts.Length == 5 ? parts[4] : "")
        {
            case ("GET", ""):
                return Json(200, Describe(d));
            case ("POST", "pause"):
                await queue.PauseAsync(id);
                return Json(200, Describe(d));
            case ("POST", "resume"):
                queue.Resume(id);
                return Json(200, Describe(d));
            case ("DELETE", ""):
                var files = req.Query.Split('&').Contains("files=1");
                await queue.RemoveAsync(id, files);
                return Json(200, Ok());
        }
        return Json(405, Error("method"));
    }

    /// {"url": "...", "folder": "D:\\x", "name": "a.zip", "start": true}; a magnet link adds a torrent.
    private Response Add(JsonObject? o)
    {
        var url = o?["url"]?.GetValue<string>()?.Trim();
        if (url is null || !(url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
                             || Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            return Json(400, Error("url"));
        var folder = o?["folder"]?.GetValue<string>() is { Length: > 0 } f && Path.IsPathFullyQualified(f) ? f : null;
        var name = o?["name"]?.GetValue<string>() is { Length: > 0 } n ? Platform.Windows.FileNameSanitizer.Sanitize(n) : null;
        var start = o?["start"]?.GetValue<bool>() ?? true;
        IDownload? d = url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? _engine.AddTorrent(null, url, saveDirectory: folder)
            : _engine.AddHttp(url, name, saveDirectory: folder, start: start);
        if (d is null) return Json(409, Error("duplicate"));
        Log.Information("Remote: added {Id}", d.Id);
        return Json(200, Describe(d));
    }

    private JsonObject Stats()
    {
        var items = _engine.Queue.Items;
        return new JsonObject
        {
            ["downloadSpeed"] = items.Where(d => d.State == DownloadState.Downloading).Sum(d => d.GetProgress().BytesPerSecond),
            ["uploadSpeed"] = items.OfType<TorrentDownload>().Sum(t => t.UploadRate),
            ["active"] = items.Count(d => d.State is DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.Seeding),
            ["waiting"] = items.Count(d => d.State is DownloadState.Queued or DownloadState.Scheduled),
            ["total"] = items.Count,
            ["held"] = _engine.Queue.Held,
        };
    }

    public static string StateName(DownloadState s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    public static JsonObject Describe(IDownload d)
    {
        var r = d.Record;
        var p = d.GetProgress();
        return new JsonObject
        {
            ["id"] = d.Id,
            ["name"] = d.FileName,
            ["kind"] = d.Kind.ToString().ToLowerInvariant(),
            ["state"] = StateName(d.State),
            ["size"] = p.TotalBytes,
            ["done"] = p.BytesDone,
            ["progress"] = Math.Round(d.State == DownloadState.Completed ? 1 : p.Fraction, 4),
            ["speed"] = d.State == DownloadState.Downloading ? p.BytesPerSecond : 0,
            ["eta"] = p.Eta is { } eta ? (long)eta.TotalSeconds : null,
            ["url"] = r.Http?.Url ?? r.Media?.PageUrl ?? r.Torrent?.Magnet,
            ["folder"] = r.SaveDirectory,
            ["category"] = r.Category,
            ["tags"] = new JsonArray([.. r.Tags.Select(t => (JsonNode)t)]),
            ["queue"] = r.QueueId,
            ["error"] = r.Error,
            ["addedAt"] = r.CreatedAt,
            ["completedAt"] = r.CompletedAt,
        };
    }

    private static JsonObject Ok() => new() { ["ok"] = true };
    private static JsonObject Error(string code) => new() { ["error"] = code };
    private static Response Json(int status, JsonNode body) => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body.ToJsonString()));

    // ================= web page =================

    private static readonly Lazy<byte[]> WebUi = new(() =>
    {
        using var s = typeof(RemoteApi).Assembly.GetManifestResourceStream("Fluxer.Core.Remote.webui.html")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    /// The page only talks to this server, and nothing may frame it.
    private static readonly Dictionary<string, string> WebUiHeaders = new()
    {
        ["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
        ["X-Frame-Options"] = "DENY",
        ["Referrer-Policy"] = "no-referrer",
    };

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
    }
}

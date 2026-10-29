using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Fluxer.Core.Remote;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class RemoteApiTests : IAsyncLifetime
{
    private const string Key = "test-key-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fluxer-remote-" + Guid.NewGuid().ToString("N"));
    private FluxerEngine _engine = null!;
    private RemoteApi _api = null!;
    private readonly HttpClient _http = new();
    private string Base => $"http://127.0.0.1:{_api.Port}";

    public ValueTask InitializeAsync()
    {
        _engine = new FluxerEngine(_dir);
        _engine.Settings.DownloadDirectory = _dir;
        _api = new RemoteApi(_engine, Key, lan: false, version: "1.2.3");
        _api.Start(0);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _api.DisposeAsync();
        await _engine.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Rest(HttpMethod method, string path, object? body = null, string? key = Key, string? host = null)
    {
        using var req = new HttpRequestMessage(method, Base + path);
        if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (host is not null) req.Headers.Host = host;
        if (body is JsonNode n) req.Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, TestContext.Current.CancellationToken);
        var text = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (res.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private async Task<JsonNode> Rpc(string method, params JsonNode?[] ps)
    {
        var call = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "q", ["method"] = method, ["params"] = new JsonArray(ps) };
        using var res = await _http.PostAsync(Base + "/jsonrpc", new StringContent(call.ToJsonString(), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var text = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"), $"{(int)res.StatusCode} {res.Headers} {res.Content.Headers} {text}");
        return JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task EveryCallNeedsTheKey_AndAnotherHostNameIsRefused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rest(HttpMethod.Get, "/api/v1/downloads", key: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rest(HttpMethod.Get, "/api/v1/downloads", key: "wrong")).Status);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await Rest(HttpMethod.Get, "/api/v1/version", host: "evil.example:80")).Status);
        var (status, body) = await Rest(HttpMethod.Get, "/api/v1/version");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("1.2.3", body!["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task WrongKeys_BlockTheCallerForAMinute()
    {
        for (var i = 0; i < 10; i++) await Rest(HttpMethod.Get, "/api/v1/version", key: "wrong" + i);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Rest(HttpMethod.Get, "/api/v1/version")).Status);
    }

    [Fact]
    public async Task WrongTokensInsideAMulticall_CountToo()
    {
        var guesses = new JsonArray([.. Enumerable.Range(0, 12).Select(i => (JsonNode)new JsonObject
        {
            ["methodName"] = "aria2.tellActive",
            ["params"] = new JsonArray("token:guess" + i),
        })]);
        await Rpc("system.multicall", guesses);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Rest(HttpMethod.Get, "/api/v1/version")).Status);
    }

    [Fact]
    public async Task Rest_AddPauseResumeRemove()
    {
        var (status, added) = await Rest(HttpMethod.Post, "/api/v1/downloads", new JsonObject { ["url"] = "http://127.0.0.1:9/file.bin", ["start"] = false, ["name"] = "../../b.bin" });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = added!["id"]!.GetValue<long>();
        Assert.Equal("b.bin", added["name"]!.GetValue<string>());     // a name, never a path
        Assert.Equal("paused", added["state"]!.GetValue<string>());           // "start": false

        Assert.Equal(HttpStatusCode.BadRequest, (await Rest(HttpMethod.Post, "/api/v1/downloads", new JsonObject { ["url"] = "file:///C:/Windows/win.ini" })).Status);
        var resumed = (await Rest(HttpMethod.Post, $"/api/v1/downloads/{id}/resume")).Body!["state"]!.GetValue<string>();
        Assert.Contains(resumed, new[] { "queued", "starting", "downloading", "failed" });
        // Nothing listens on port 9, so it may already have failed by now.
        Assert.Contains((await Rest(HttpMethod.Post, $"/api/v1/downloads/{id}/pause")).Body!["state"]!.GetValue<string>(), new[] { "paused", "failed" });

        var list = (await Rest(HttpMethod.Get, "/api/v1/downloads")).Body!["downloads"]!.AsArray();
        Assert.Single(list);
        Assert.Equal(HttpStatusCode.OK, (await Rest(HttpMethod.Delete, $"/api/v1/downloads/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Rest(HttpMethod.Get, $"/api/v1/downloads/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Rest(HttpMethod.Get, "/api/v1/nothing")).Status);
    }

    [Fact]
    public async Task Aria2_TokenIsRequired_AndAddUriGivesAGid()
    {
        var denied = await Rpc("aria2.tellActive");
        Assert.Equal("Unauthorized", denied["error"]!["message"]!.GetValue<string>());
        Assert.Equal("Unauthorized", (await Rpc("aria2.getVersion", "token:nope"))["error"]!["message"]!.GetValue<string>());

        // Cookies are stored DPAPI-encrypted, which exists only on Windows.
        var headers = OperatingSystem.IsWindows() ? new JsonArray("Cookie: s=1", "Referer: https://example.org/") : new JsonArray("Referer: https://example.org/");
        var add = await Rpc("aria2.addUri", "token:" + Key, new JsonArray("http://127.0.0.1:9/b.iso", "http://127.0.0.2:9/b.iso"),
            new JsonObject { ["out"] = "b.iso", ["header"] = headers });
        var gid = add["result"]!.GetValue<string>();
        Assert.Equal(16, gid.Length);
        Assert.Equal("q", add["id"]!.GetValue<string>());

        var download = _engine.Queue.Items.Single();
        Assert.Equal(("b.iso", "https://example.org/"), (download.FileName, download.Record.Http!.Referrer));
        if (OperatingSystem.IsWindows()) Assert.Equal("s=1", download.Record.Http.Cookies);
        Assert.Equal(["http://127.0.0.2:9/b.iso"], download.Record.Http.Mirrors);

        await _engine.Queue.PauseAsync(download.Id);
        var status = (await Rpc("aria2.tellStatus", "token:" + Key, gid, new JsonArray("gid", "status", "files")))["result"]!.AsObject();
        Assert.Equal(["gid", "status", "files"], status.Select(p => p.Key));
        Assert.Equal("paused", status["status"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:9/b.iso", status["files"]![0]!["uris"]![0]!["uri"]!.GetValue<string>());

        var waiting = (await Rpc("aria2.tellWaiting", "token:" + Key, 0, 10))["result"]!.AsArray();
        Assert.Single(waiting);
        var stat = (await Rpc("aria2.getGlobalStat", "token:" + Key))["result"]!;
        Assert.Equal("1", stat["numWaiting"]!.GetValue<string>());
        Assert.Contains("aria2.addUri", (await Rpc("system.listMethods"))["result"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal(-32601, (await Rpc("aria2.nope", "token:" + Key))["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Aria2_ChangePosition_FollowsTheWaitingQueue()
    {
        var ids = Enumerable.Range(0, 4).Select(i => _engine.AddHttp($"http://127.0.0.1:9/{i}.bin", $"{i}.bin", start: false).Id).ToList();
        string Gid(long id) => id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        string Order() => string.Concat(_engine.Queue.Items.OrderBy(d => d.Record.QueueOrder).Select(d => d.FileName[0]));
        var token = "token:" + Key;

        Assert.Equal(2, (await Rpc("aria2.changePosition", token, Gid(ids[0]), 2, "POS_SET"))["result"]!.GetValue<int>());
        Assert.Equal("1203", Order());
        Assert.Equal(1, (await Rpc("aria2.changePosition", token, Gid(ids[0]), -1, "POS_CUR"))["result"]!.GetValue<int>());
        Assert.Equal("1023", Order());
        Assert.Equal(3, (await Rpc("aria2.changePosition", token, Gid(ids[1]), 0, "POS_END"))["result"]!.GetValue<int>());
        Assert.Equal("0231", Order());
    }

    [Fact]
    public async Task Aria2_MulticallAndBatch()
    {
        var calls = new JsonArray(
            new JsonObject { ["methodName"] = "aria2.getVersion", ["params"] = new JsonArray("token:" + Key) },
            new JsonObject { ["methodName"] = "aria2.tellStatus", ["params"] = new JsonArray("token:" + Key, "00000000000000ff") });
        var multi = (await Rpc("system.multicall", calls))["result"]!.AsArray();
        Assert.Equal("1.37.0", multi[0]![0]!["version"]!.GetValue<string>());
        Assert.Contains("not found", multi[1]!["message"]!.GetValue<string>());

        var batch = new JsonArray(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "aria2.getGlobalStat", ["params"] = new JsonArray("token:" + Key) },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "aria2.getSessionInfo", ["params"] = new JsonArray("token:" + Key) });
        using var res = await _http.PostAsync(Base + "/jsonrpc", new StringContent(batch.ToJsonString()), TestContext.Current.CancellationToken);
        var replies = JsonNode.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsArray();
        Assert.Equal([1, 2], replies.Select(r => r!["id"]!.GetValue<int>()));
    }

    [Fact]
    public async Task WebPage_IsServedWithAStrictPolicy_AndPreflightIsAnswered()
    {
        using var page = await _http.GetAsync(Base + "/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("/api/v1/", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var pre = new HttpRequestMessage(HttpMethod.Options, Base + "/jsonrpc");
        using var res = await _http.SendAsync(pre, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task OversizedOrMalformedRequests_AreRefused()
    {
        // The server answers before reading such a body, so this goes over a raw socket.
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _api.Port, TestContext.Current.CancellationToken);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /jsonrpc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {MiniHttpServer.MaxBodyBytes + 1}\r\n\r\n"), TestContext.Current.CancellationToken);
            using var reader = new StreamReader(stream);
            Assert.StartsWith("HTTP/1.1 413", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        }
        using var bad = await _http.PostAsync(Base + "/jsonrpc", new StringContent("{nope"), TestContext.Current.CancellationToken);
        Assert.Equal(-32700, JsonNode.Parse(await bad.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]!["code"]!.GetValue<int>());
    }
}

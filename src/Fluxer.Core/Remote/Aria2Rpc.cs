using System.Globalization;
using System.Text.Json.Nodes;
using Fluxer.Core.Downloads;
using Fluxer.Core.Torrent;

namespace Fluxer.Core.Remote;

/// The part of aria2's JSON-RPC that front-ends (AriaNg and the like) and "send to aria2" browser extensions use,
/// mapped onto Fluxer's list. A download's GID is its id in 16 hex digits; numbers are strings, as in aria2.
/// The secret is Fluxer's API key, passed as "token:KEY" first in params.
internal sealed class Aria2Rpc(FluxerEngine engine, Func<string?, bool> tokenOk, string version)
{
    private const int Failure = 1;      // aria2's generic error code
    private static readonly string SessionId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));

    private static readonly string[] Methods =
    [
        "aria2.addUri", "aria2.addTorrent", "aria2.remove", "aria2.forceRemove", "aria2.pause", "aria2.forcePause",
        "aria2.pauseAll", "aria2.forcePauseAll", "aria2.unpause", "aria2.unpauseAll", "aria2.tellStatus", "aria2.getUris",
        "aria2.getFiles", "aria2.getPeers", "aria2.getServers", "aria2.tellActive", "aria2.tellWaiting", "aria2.tellStopped",
        "aria2.changePosition", "aria2.changeOption", "aria2.getOption", "aria2.getGlobalOption", "aria2.changeGlobalOption",
        "aria2.getGlobalStat", "aria2.purgeDownloadResult", "aria2.removeDownloadResult", "aria2.getVersion",
        "aria2.getSessionInfo", "aria2.saveSession", "system.multicall", "system.listMethods", "system.listNotifications",
    ];

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// One request or a batch. Unauthorized: some call carried a wrong token (counts towards blocking the caller).
    public async Task<(JsonNode? Reply, bool Unauthorized)> HandleAsync(JsonNode? body)
    {
        var unauthorized = false;
        async Task<JsonNode> One(JsonNode? call)
        {
            var id = call?["id"]?.DeepClone();
            if (call is not JsonObject o || o["method"]?.GetValueKind() != System.Text.Json.JsonValueKind.String)
                return ErrorReply(id, -32600, "Invalid Request");
            try
            {
                var result = await CallAsync(o["method"]!.GetValue<string>(), o["params"] as JsonArray ?? [], top: true);
                return new JsonObject { ["id"] = id, ["jsonrpc"] = "2.0", ["result"] = result };
            }
            catch (RpcError e)
            {
                if (e.Message == "Unauthorized") unauthorized = true;
                return ErrorReply(id, e.Code, e.Message);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or OverflowException)
            {
                return ErrorReply(id, Failure, "Invalid parameters");
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "aria2 call {Method} failed", o["method"]);
                return ErrorReply(id, Failure, "Internal error");
            }
        }
        if (body is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var call in batch.Take(100)) replies.Add(await One(call));
            return (replies, unauthorized);
        }
        return (await One(body), unauthorized);
    }

    public static JsonObject ErrorReply(JsonNode? id, int code, string message) =>
        new() { ["id"] = id, ["jsonrpc"] = "2.0", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task<JsonNode?> CallAsync(string method, JsonArray ps, bool top)
    {
        var args = ps.Select(p => p?.DeepClone()).ToList();
        if (method.StartsWith("system.", StringComparison.Ordinal))
        {
            return method switch
            {
                "system.listMethods" => Strings(Methods),
                "system.listNotifications" => new JsonArray(),
                "system.multicall" when top => await MulticallAsync(args.FirstOrDefault() as JsonArray ?? []),
                _ => throw new RpcError(-32601, "Method not found"),
            };
        }
        if (args.Count == 0 || args[0]?.GetValueKind() != System.Text.Json.JsonValueKind.String || !tokenOk(args[0]!.GetValue<string>()))
            throw new RpcError(Failure, "Unauthorized");
        args.RemoveAt(0);
        string Arg(int i) => args.ElementAtOrDefault(i)?.GetValue<string>() ?? throw new RpcError(Failure, "Invalid parameters");
        var queue = engine.Queue;
        switch (method)
        {
            case "aria2.addUri":
                return Gid(AddUri(args.ElementAtOrDefault(0) as JsonArray, args.ElementAtOrDefault(1) as JsonObject));
            case "aria2.addTorrent":
                var options = args.ElementAtOrDefault(2) as JsonObject;
                var torrent = engine.AddTorrent(Convert.FromBase64String(Arg(0)), null, saveDirectory: Folder(options))
                              ?? throw new RpcError(Failure, "The torrent is already in the list");
                return Gid(torrent);
            case "aria2.remove" or "aria2.forceRemove":
            {
                var d = Find(Arg(0));
                await queue.RemoveAsync(d.Id, deleteFiles: false);
                return GidOf(d);
            }
            case "aria2.pause" or "aria2.forcePause":
            {
                var d = Find(Arg(0));
                await queue.PauseAsync(d.Id);
                return GidOf(d);
            }
            case "aria2.unpause":
            {
                var d = Find(Arg(0));
                queue.Resume(d.Id);
                return GidOf(d);
            }
            case "aria2.pauseAll" or "aria2.forcePauseAll":
                await queue.PauseAllAsync();
                return "OK";
            case "aria2.unpauseAll":
                queue.ResumeAll();
                return "OK";
            case "aria2.tellStatus":
                return Status(Find(Arg(0)), Keys(args.ElementAtOrDefault(1)));
            case "aria2.getUris":
                return Uris(Find(Arg(0)));
            case "aria2.getFiles":
                return Files(Find(Arg(0)));
            case "aria2.getPeers" or "aria2.getServers":
                Find(Arg(0));
                return new JsonArray();
            case "aria2.tellActive":
                return List(queue.Items.Where(d => AriaStatus(d.State) == "active"), Keys(args.ElementAtOrDefault(0)));
            case "aria2.tellWaiting":
                return Page(queue.Items.Where(d => AriaStatus(d.State) is "waiting" or "paused").OrderBy(d => d.Record.QueueOrder), args);
            case "aria2.tellStopped":
                return Page(queue.Items.Where(d => AriaStatus(d.State) is "complete" or "error"), args);
            case "aria2.changePosition":
            {
                var d = Find(Arg(0));
                var how = Arg(2);
                queue.Reorder([d.Id], how == "POS_END" ? DownloadQueue.Move.Bottom : DownloadQueue.Move.Top);
                return how == "POS_END" ? queue.Items.Count - 1 : 0;
            }
            case "aria2.changeOption" or "aria2.changeGlobalOption" or "aria2.saveSession":
                return "OK";
            case "aria2.getOption":
            {
                var d = Find(Arg(0));
                return new JsonObject { ["dir"] = d.Record.SaveDirectory, ["out"] = d.FileName };
            }
            case "aria2.getGlobalOption":
            {
                var s = engine.Settings;
                return new JsonObject
                {
                    ["dir"] = s.DownloadDirectory,
                    ["max-concurrent-downloads"] = Num(s.HttpSlots),
                    ["max-overall-download-limit"] = Num(s.HttpLimit),
                    ["split"] = Num(s.ConnectionsPerDownload),
                };
            }
            case "aria2.getGlobalStat":
            {
                var items = queue.Items;
                var stopped = items.Count(d => AriaStatus(d.State) is "complete" or "error");
                return new JsonObject
                {
                    ["downloadSpeed"] = Num(items.Where(d => d.State == DownloadState.Downloading).Sum(d => d.GetProgress().BytesPerSecond)),
                    ["uploadSpeed"] = Num(items.OfType<TorrentDownload>().Sum(t => t.UploadRate)),
                    ["numActive"] = Num(items.Count(d => AriaStatus(d.State) == "active")),
                    ["numWaiting"] = Num(items.Count(d => AriaStatus(d.State) is "waiting" or "paused")),
                    ["numStopped"] = Num(stopped),
                    ["numStoppedTotal"] = Num(stopped),
                };
            }
            case "aria2.purgeDownloadResult":
                // Clears finished and failed entries from the list, as aria2 forgets them; files stay.
                foreach (var d in queue.Items.Where(d => d.State is DownloadState.Completed or DownloadState.Failed))
                    await queue.RemoveAsync(d.Id, deleteFiles: false);
                return "OK";
            case "aria2.removeDownloadResult":
            {
                var d = Find(Arg(0));
                if (d.State is not (DownloadState.Completed or DownloadState.Failed)) throw new RpcError(Failure, "Download is not stopped");
                await queue.RemoveAsync(d.Id, deleteFiles: false);
                return "OK";
            }
            case "aria2.getVersion":
                return new JsonObject { ["version"] = "1.37.0", ["enabledFeatures"] = Strings(["BitTorrent", "HTTPS", "Message Digest"]), ["fluxer"] = version };
            case "aria2.getSessionInfo":
                return new JsonObject { ["sessionId"] = SessionId };
            default:
                throw new RpcError(-32601, "Method not found");
        }
    }

    private async Task<JsonArray> MulticallAsync(JsonArray calls)
    {
        var results = new JsonArray();
        foreach (var c in calls.Take(100))
        {
            try
            {
                var name = c?["methodName"]?.GetValue<string>() ?? throw new RpcError(-32600, "Invalid Request");
                // (JsonNode): JsonArray.Add<T> would pick the reflection-based overload, which trimming breaks.
                results.Add((JsonNode)new JsonArray(await CallAsync(name, c["params"] as JsonArray ?? [], top: false)));
            }
            catch (RpcError e) { results.Add((JsonNode)new JsonObject { ["code"] = e.Code, ["message"] = e.Message }); }
            catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or OverflowException)
            {
                results.Add((JsonNode)new JsonObject { ["code"] = Failure, ["message"] = "Invalid parameters" });
            }
        }
        return results;
    }

    /// The first address is the download; the others are mirrors of the same file.
    private IDownload AddUri(JsonArray? uris, JsonObject? options)
    {
        var list = uris?.Select(u => u?.GetValue<string>()).OfType<string>().Select(u => u.Trim()).Where(u => u.Length > 0).ToList() ?? [];
        if (list.Count == 0) throw new RpcError(Failure, "No URI to download.");
        var first = list[0];
        if (first.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return engine.AddTorrent(null, first, saveDirectory: Folder(options)) ?? throw new RpcError(Failure, "The torrent is already in the list");
        if (!Uri.TryCreate(first, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) throw new RpcError(Failure, "Unsupported URI");
        var name = options?["out"]?.GetValue<string>() is { Length: > 0 } o ? Platform.Windows.FileNameSanitizer.Sanitize(Path.GetFileName(o)) : null;
        // Browser extensions pass the page, the browser's User-Agent and cookies along, also as "header" lines.
        string? referer = options?["referer"]?.GetValue<string>(), agent = options?["user-agent"]?.GetValue<string>(), cookies = null;
        foreach (var line in (options?["header"] as JsonArray)?.Select(h => h?.GetValue<string>()).OfType<string>() ?? [])
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var (key, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
            if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) cookies = value;
            else if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) referer ??= value;
            else if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) agent ??= value;
        }
        var ctx = referer is { Length: > 0 } || agent is { Length: > 0 } || cookies is { Length: > 0 } ? new RequestContext(referer, agent, cookies) : null;
        return engine.AddHttp(first, name, ctx, Folder(options), mirrors: list.Skip(1));
    }

    private static string? Folder(JsonObject? options) =>
        options?["dir"]?.GetValue<string>() is { Length: > 0 } dir && Path.IsPathFullyQualified(dir) ? dir : null;

    private IDownload Find(string gid)
    {
        if (!long.TryParse(gid, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) || engine.Queue.Find(id) is not { } d)
            throw new RpcError(Failure, $"GID {gid} is not found");
        return d;
    }

    private static string GidOf(IDownload d) => d.Id.ToString("x16", CultureInfo.InvariantCulture);
    private static JsonNode Gid(IDownload d) => GidOf(d);
    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static JsonArray Strings(IEnumerable<string> s) => new([.. s.Select(x => (JsonNode)x)]);

    public static string AriaStatus(DownloadState s) => s switch
    {
        DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.Seeding => "active",
        DownloadState.Queued or DownloadState.Scheduled => "waiting",
        DownloadState.Paused or DownloadState.NeedsLinkRefresh or DownloadState.DiskFull => "paused",
        DownloadState.Completed => "complete",
        _ => "error",
    };

    private static HashSet<string>? Keys(JsonNode? keys) =>
        keys is JsonArray a && a.Count > 0 ? [.. a.Select(k => k?.GetValue<string>()).OfType<string>()] : null;

    private JsonArray Page(IEnumerable<IDownload> items, List<JsonNode?> args)
    {
        var list = items.ToList();
        var offset = args.ElementAtOrDefault(0)?.GetValue<int>() ?? 0;
        var num = Math.Clamp(args.ElementAtOrDefault(1)?.GetValue<int>() ?? 1000, 0, 1000);
        // A negative offset counts from the end, walking backwards, as in aria2.
        IEnumerable<IDownload> page = offset >= 0
            ? list.Skip(offset).Take(num)
            : Enumerable.Reverse(list).Skip(-offset - 1).Take(num);
        return List(page, Keys(args.ElementAtOrDefault(2)));
    }

    private JsonArray List(IEnumerable<IDownload> items, HashSet<string>? keys) => new([.. items.Select(d => (JsonNode)Status(d, keys))]);

    private static JsonObject Status(IDownload d, HashSet<string>? keys)
    {
        var p = d.GetProgress();
        var r = d.Record;
        var torrent = d as TorrentDownload;
        var all = new JsonObject
        {
            ["gid"] = GidOf(d),
            ["status"] = AriaStatus(d.State),
            ["totalLength"] = Num(p.TotalBytes ?? 0),
            ["completedLength"] = Num(d.State == DownloadState.Completed ? p.TotalBytes ?? p.BytesDone : p.BytesDone),
            ["uploadLength"] = Num(torrent?.UploadedBytes ?? 0),
            ["downloadSpeed"] = Num(d.State == DownloadState.Downloading ? p.BytesPerSecond : 0),
            ["uploadSpeed"] = Num(torrent?.UploadRate ?? 0),
            ["connections"] = Num(p.Connections),
            ["dir"] = r.SaveDirectory,
            ["files"] = Files(d),
        };
        if (d.State == DownloadState.Failed)
        {
            all["errorCode"] = "1";
            all["errorMessage"] = r.Error ?? "";
        }
        if (r.Torrent is { } t)
        {
            all["infoHash"] = t.InfoHash;
            all["bittorrent"] = new JsonObject { ["info"] = new JsonObject { ["name"] = d.FileName } };
            all["seeder"] = d.State == DownloadState.Seeding ? "true" : "false";
        }
        if (keys is null) return all;
        var some = new JsonObject();
        foreach (var (k, v) in all) if (keys.Contains(k)) some[k] = v?.DeepClone();
        return some;
    }

    private static JsonArray Files(IDownload d)
    {
        var p = d.GetProgress();
        var r = d.Record;
        return new JsonArray(new JsonObject
        {
            ["index"] = "1",
            ["path"] = d.FileName.Length > 0 ? r.FullPath : "",
            ["length"] = Num(p.TotalBytes ?? 0),
            ["completedLength"] = Num(d.State == DownloadState.Completed ? p.TotalBytes ?? p.BytesDone : p.BytesDone),
            ["selected"] = "true",
            ["uris"] = Uris(d),
        });
    }

    private static JsonArray Uris(IDownload d) =>
        (d.Record.Http?.Url ?? d.Record.Media?.PageUrl) is { } url ? new JsonArray(new JsonObject { ["uri"] = url, ["status"] = "used" }) : new JsonArray();
}

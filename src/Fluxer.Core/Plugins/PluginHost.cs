using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fluxer.Core.Downloads;
using Fluxer.Core.Remote;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Serilog;

namespace Fluxer.Core.Plugins;

/// JavaScript plugins from the data folder's "plugins" folder. Each .js file starts with a userscript-style header
/// that names it and lists the permissions it needs; it runs only after the user turned it on, and only while the
/// file is byte for byte the one they approved (an edited file is off again until approved anew).
///
/// Scripts get one global, `fluxer`, and nothing of .NET: on(event, fn) for "added", "completed" and "failed",
/// log(), and a small store; notify() needs "notify", add/list/pause/resume need "downloads", http() needs "http".
/// Each runs in its own interpreter with limits on time, statements, memory and recursion, one call at a time.
public sealed partial class PluginHost : IDisposable
{
    public static readonly string[] Permissions = ["notify", "downloads", "http"];
    public static readonly string[] Events = ["added", "completed", "failed"];
    private const int MaxStoreBytes = 64 * 1024;
    private const int MaxHttpBytes = 1024 * 1024;

    public sealed record Manifest(string Name, string Version, string Description, IReadOnlyList<string> Permissions);

    public sealed class Plugin
    {
        public required string File { get; init; }
        public required string Hash { get; init; }
        public required Manifest Manifest { get; init; }
        public bool Enabled { get; internal set; }
        /// Last load or run error, for Settings.
        public string? Error { get; internal set; }
        internal Engine? Js;
        internal readonly Dictionary<string, JsValue> Handlers = [];
        internal readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly string _folder;
    private readonly FluxerEngine _engine;
    private readonly Action<string, string> _notify;
    private List<Plugin> _plugins = [];

    public PluginHost(string folder, FluxerEngine engine, Action<string, string> notify)
    {
        _folder = folder;
        _engine = engine;
        _notify = notify;
    }

    public string Folder => _folder;
    public IReadOnlyList<Plugin> Plugins => _plugins;
    /// A plugin's run failed (Settings shows Error).
    public event Action<Plugin>? Faulted;

    [GeneratedRegex(@"^\s*//\s*==FluxerPlugin==\s*$(?<body>.*?)^\s*//\s*==/FluxerPlugin==", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex HeaderBlock();
    [GeneratedRegex(@"^\s*//\s*@(?<key>\w+)\s+(?<value>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLine();

    /// Null when the file has no header block.
    public static Manifest? ParseManifest(string source, string fileName)
    {
        if (HeaderBlock().Match(source) is not { Success: true } block) return null;
        string? name = null, version = null, description = null;
        var permissions = new List<string>();
        foreach (Match m in HeaderLine().Matches(block.Groups["body"].Value))
        {
            var value = m.Groups["value"].Value;
            switch (m.Groups["key"].Value.ToLowerInvariant())
            {
                case "name": name = value; break;
                case "version": version = value; break;
                case "description": description = value; break;
                case "permission":
                    foreach (var p in value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
                        if (Permissions.Contains(p.ToLowerInvariant()) && !permissions.Contains(p.ToLowerInvariant())) permissions.Add(p.ToLowerInvariant());
                    break;
            }
        }
        return new Manifest(name ?? Path.GetFileNameWithoutExtension(fileName), version ?? "", description ?? "", permissions);
    }

    public static string HashOf(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// Reads the folder again and starts the plugins the user approved (file name and hash both match).
    public void Load(IEnumerable<PluginGrant> grants)
    {
        Unload();
        if (!Directory.Exists(_folder)) return;
        var approved = grants.Select(g => (g.File.ToLowerInvariant(), g.Hash)).ToHashSet();
        var list = new List<Plugin>();
        foreach (var path in Directory.EnumerateFiles(_folder, "*.js").Order(StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(path); }
            catch (IOException) { continue; }
            if (bytes.Length > 512 * 1024) continue;
            var file = Path.GetFileName(path);
            var source = Encoding.UTF8.GetString(bytes);
            if (ParseManifest(source, file) is not { } manifest) continue;
            var plugin = new Plugin { File = file, Hash = HashOf(bytes), Manifest = manifest };
            list.Add(plugin);
            if (approved.Contains((file.ToLowerInvariant(), plugin.Hash))) Start(plugin, source);
        }
        _plugins = list;
    }

    private void Start(Plugin plugin, string source)
    {
        try
        {
            plugin.Js = new Engine(o => o
                .Strict()
                .LimitMemory(64 * 1024 * 1024)
                .TimeoutInterval(TimeSpan.FromSeconds(30))
                .MaxStatements(10_000_000)
                .LimitRecursion(256));
            plugin.Js.SetValue("fluxer", Api(plugin));
            plugin.Js.Execute(source);
            plugin.Enabled = true;
            plugin.Error = null;
            Log.Information("Plugin {Name} {Version} started", plugin.Manifest.Name, plugin.Manifest.Version);
        }
        catch (Exception e)
        {
            plugin.Js?.Dispose();
            plugin.Js = null;
            plugin.Handlers.Clear();
            plugin.Enabled = false;
            plugin.Error = Describe(e);
            Log.Warning("Plugin {File} did not start: {Error}", plugin.File, plugin.Error);
        }
    }

    /// Runs the plugins' handlers for a download event, each plugin on its own, off the caller's thread.
    public void Raise(string evt, DownloadRecord record)
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled && p.Handlers.ContainsKey(evt)))
            _ = Task.Run(async () =>
            {
                await plugin.Gate.WaitAsync();
                try
                {
                    if (plugin.Js is not { } js || !plugin.Handlers.TryGetValue(evt, out var handler)) return;
                    js.Invoke(handler, Payload(js, record, evt));
                }
                catch (Exception e)
                {
                    plugin.Error = Describe(e);
                    Log.Warning("Plugin {Name} failed on {Event}: {Error}", plugin.Manifest.Name, evt, plugin.Error);
                    Faulted?.Invoke(plugin);
                }
                finally { plugin.Gate.Release(); }
            });
    }

    private static string Describe(Exception e) => e switch
    {
        JavaScriptException js => js.Message + (js.Location.Start.Line > 0 ? $" (line {js.Location.Start.Line})" : ""),
        StatementsCountOverflowException or TimeoutException => "took too long",
        MemoryLimitExceededException => "used too much memory",
        RecursionDepthOverflowException => "recursed too deep",
        _ => e.Message,
    };

    private static JsValue Payload(Engine js, DownloadRecord r, string evt)
    {
        var o = new JsonObject
        {
            ["event"] = evt,
            ["id"] = r.Id,
            ["name"] = r.FileName,
            ["path"] = r.FileName.Length > 0 ? r.FullPath : null,
            ["folder"] = r.SaveDirectory,
            ["url"] = r.Http?.Url ?? r.Media?.PageUrl ?? r.Torrent?.Magnet,
            ["kind"] = r.Kind.ToString().ToLowerInvariant(),
            ["size"] = r.TotalBytes,
            ["category"] = r.Category,
            ["tags"] = new JsonArray([.. r.Tags.Select(t => (JsonNode)t)]),
            ["error"] = r.Error,
        };
        return new JsonParser(js).Parse(o.ToJsonString());
    }

    // ================= the `fluxer` object =================

    private JsObject Api(Plugin plugin)
    {
        var js = plugin.Js!;
        var api = new JsObject(js);
        var name = plugin.Manifest.Name;

        void Need(string permission)
        {
            if (!plugin.Manifest.Permissions.Contains(permission))
                throw new JavaScriptException(js.Intrinsics.Error, $"this plugin did not ask for the \"{permission}\" permission");
        }
        static string Text(JsValue[] args, int i) => args.Length > i && !args[i].IsUndefined() && !args[i].IsNull() ? args[i].ToString() : "";
        void Fn(JsObject target, string fn, Func<JsValue[], JsValue> body) =>
            target.Set(fn, new ClrFunction(js, fn, (_, args) => body(args)));
        JsValue FromJson(JsonNode node) => new JsonParser(js).Parse(node.ToJsonString());

        Fn(api, "on", args =>
        {
            var evt = Text(args, 0);
            if (!Events.Contains(evt)) throw new JavaScriptException(js.Intrinsics.TypeError, $"unknown event \"{evt}\"; use {string.Join(", ", Events)}");
            if (args.Length < 2 || args[1] is not Jint.Native.Function.Function) throw new JavaScriptException(js.Intrinsics.TypeError, "on(event, function)");
            plugin.Handlers[evt] = args[1];
            return JsValue.Undefined;
        });
        Fn(api, "log", args =>
        {
            Log.Information("Plugin {Name}: {Message}", name, string.Join(" ", args.Select(a => a.ToString())));
            return JsValue.Undefined;
        });
        Fn(api, "notify", args =>
        {
            Need("notify");
            _notify(Text(args, 0), Text(args, 1));
            return JsValue.Undefined;
        });

        Fn(api, "add", args =>
        {
            Need("downloads");
            var url = Text(args, 0).Trim();
            var options = args.Length > 1 && args[1] is JsObject o ? o : null;
            string? Opt(string key) => options?.Get(key) is { } v && !v.IsUndefined() && !v.IsNull() ? v.ToString() : null;
            var folder = Opt("folder") is { } f && Path.IsPathFullyQualified(f) ? f : null;
            var file = Opt("name") is { Length: > 0 } n ? Platform.Windows.FileNameSanitizer.Sanitize(n) : null;
            IDownload? d = url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
                ? _engine.AddTorrent(null, url, saveDirectory: folder)
                : Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    ? _engine.AddHttp(url, file, saveDirectory: folder)
                    : throw new JavaScriptException(js.Intrinsics.TypeError, "add(url): an http(s) or magnet link");
            Log.Information("Plugin {Name} added {Id}", name, d?.Id);
            return d is null ? JsValue.Null : (JsValue)d.Id;
        });
        Fn(api, "list", _ =>
        {
            Need("downloads");
            return FromJson(new JsonArray([.. _engine.Queue.Items.Select(d => (JsonNode)RemoteApi.Describe(d))]));
        });
        Fn(api, "pause", args =>
        {
            Need("downloads");
            _engine.Queue.PauseAsync((long)args.ElementAtOrDefault(0)!.AsNumber()).GetAwaiter().GetResult();
            return JsValue.Undefined;
        });
        Fn(api, "resume", args =>
        {
            Need("downloads");
            _engine.Queue.Resume((long)args.ElementAtOrDefault(0)!.AsNumber());
            return JsValue.Undefined;
        });

        // http({url, method, headers, body}) → {status, headers, body}; synchronous, 15 s, at most 1 MB back.
        Fn(api, "http", args =>
        {
            Need("http");
            var o = args.Length > 0 && args[0] is JsObject r ? r : throw new JavaScriptException(js.Intrinsics.TypeError, "http({url, method, headers, body})");
            var url = o.Get("url").ToString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                throw new JavaScriptException(js.Intrinsics.TypeError, "http: an http(s) url");
            var method = o.Get("method") is { } m && m.IsString() ? m.AsString().ToUpperInvariant() : "GET";
            using var req = new HttpRequestMessage(new HttpMethod(method), u);
            if (o.Get("body") is { } b && !b.IsUndefined() && !b.IsNull()) req.Content = new StringContent(b.ToString(), Encoding.UTF8);
            if (o.Get("headers") is JsObject headers)
                foreach (var key in headers.GetOwnPropertyKeys())
                {
                    var value = headers.Get(key).ToString();
                    if (!req.Headers.TryAddWithoutValidation(key.ToString(), value)) req.Content?.Headers.TryAddWithoutValidation(key.ToString(), value);
                }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var res = _engine.Http.Client.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var stream = res.Content.ReadAsStream(cts.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(chunk)) > 0 && buffer.Length < MaxHttpBytes) buffer.Write(chunk, 0, Math.Min(read, MaxHttpBytes - (int)buffer.Length));
            var result = new JsonObject
            {
                ["status"] = (int)res.StatusCode,
                ["body"] = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length),
                ["headers"] = new JsonObject([.. res.Headers.Concat(res.Content.Headers)
                    .Select(h => KeyValuePair.Create(h.Key.ToLowerInvariant(), (JsonNode?)string.Join(", ", h.Value)))
                    .DistinctBy(h => h.Key)]),
            };
            return FromJson(result);
        });

        // store.get(key) / store.set(key, value): a few strings kept across restarts, 64 KB in all.
        var store = new JsObject(js);
        var storeKey = "plugin-store:" + plugin.File.ToLowerInvariant();
        Fn(store, "get", args =>
        {
            var all = _engine.Db.GetSetting<Dictionary<string, string>>(storeKey);
            return all.TryGetValue(Text(args, 0), out var v) ? v : JsValue.Undefined;
        });
        Fn(store, "set", args =>
        {
            var all = _engine.Db.GetSetting<Dictionary<string, string>>(storeKey);
            var key = Text(args, 0);
            if (args.Length < 2 || args[1].IsUndefined() || args[1].IsNull()) all.Remove(key);
            else all[key] = args[1].ToString();
            if (all.Sum(p => p.Key.Length + p.Value.Length) * 2 > MaxStoreBytes)
                throw new JavaScriptException(js.Intrinsics.Error, "store is full (64 KB)");
            _engine.Db.SetSetting(storeKey, all);
            return JsValue.Undefined;
        });
        api.Set("store", store);
        api.Set("version", "1");
        return api;
    }

    /// Writes a commented example (off until approved) and returns its path.
    public string WriteExample()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "example-notify-big.js");
        if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, Example);
        return path;
    }

    private const string Example = """
        // ==FluxerPlugin==
        // @name        Big download notice
        // @version     1.0
        // @description Shows a notification when a download over 1 GB finishes, and counts them.
        // @permission  notify
        // ==/FluxerPlugin==

        // Events: "added", "completed", "failed". The download has id, name, path, folder, url, kind,
        // size, category, tags and error.
        fluxer.on("completed", d => {
          if (!d.size || d.size < 1024 * 1024 * 1024) return;
          const count = Number(fluxer.store.get("count") || 0) + 1;
          fluxer.store.set("count", String(count));
          fluxer.notify("Big download done", `${d.name} (${(d.size / 1073741824).toFixed(1)} GB), #${count}`);
        });

        // Other calls: fluxer.log(...), fluxer.add(url, { folder, name }), fluxer.list(), fluxer.pause(id),
        // fluxer.resume(id) need "downloads"; fluxer.http({ url, method, headers, body }) needs "http".
        """;

    /// Nothing new starts at once; an interpreter still inside a handler is disposed when that call ends, so
    /// reloading never waits on a slow plugin.
    private void Unload()
    {
        foreach (var p in _plugins)
        {
            p.Enabled = false;
            _ = Task.Run(async () =>
            {
                await p.Gate.WaitAsync();
                try { p.Js?.Dispose(); p.Js = null; p.Handlers.Clear(); }
                finally { p.Gate.Release(); }
            });
        }
        _plugins = [];
    }

    public void Dispose() => Unload();
}

/// A plugin file the user approved, with the content (hash) they approved.
public sealed class PluginGrant
{
    public string File { get; set; } = "";
    public string Hash { get; set; } = "";
}

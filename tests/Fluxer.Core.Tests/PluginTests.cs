using Fluxer.Core.Downloads;
using Fluxer.Core.Plugins;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class PluginTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fluxer-plugins-" + Guid.NewGuid().ToString("N"));
    private FluxerEngine _engine = null!;
    private PluginHost _host = null!;
    private readonly List<(string Title, string Body)> _notes = [];

    public ValueTask InitializeAsync()
    {
        _engine = new FluxerEngine(_dir);
        _host = new PluginHost(Path.Combine(_dir, "plugins"), _engine, (t, b) => { lock (_notes) _notes.Add((t, b)); });
        Directory.CreateDirectory(_host.Folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await _engine.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Header(string name, params string[] permissions) =>
        $"// ==FluxerPlugin==\n// @name {name}\n// @version 2.0\n" + string.Concat(permissions.Select(p => $"// @permission {p}\n")) + "// ==/FluxerPlugin==\n";

    private PluginGrant Write(string file, string source)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        File.WriteAllBytes(Path.Combine(_host.Folder, file), bytes);
        return new PluginGrant { File = file, Hash = PluginHost.HashOf(bytes) };
    }

    private string? Stored(string file, string key) =>
        _engine.Db.GetSetting<Dictionary<string, string>>("plugin-store:" + file).GetValueOrDefault(key);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private static DownloadRecord Record() =>
        new() { Id = 7, FileName = "big.iso", SaveDirectory = "/data", TotalBytes = 2L << 30, Kind = DownloadKind.Http, Http = new HttpInfo { Url = "https://example.org/big.iso" }, Tags = ["x"] };

    [Fact]
    public void Manifest_KeepsKnownPermissionsOnly()
    {
        var m = PluginHost.ParseManifest(Header("Hi", "notify", "http, filesystem", "notify"), "hi.js")!;
        Assert.Equal(("Hi", "2.0"), (m.Name, m.Version));
        Assert.Equal(["notify", "http"], m.Permissions);
        Assert.Null(PluginHost.ParseManifest("fluxer.log('no header')", "x.js"));
    }

    [Fact]
    public async Task OnlyApprovedUnchangedFilesRun()
    {
        var grant = Write("a.js", Header("A") + "fluxer.on('completed', d => fluxer.store.set('seen', d.name + ':' + d.size + ':' + d.tags[0]));");
        _host.Load([]);
        Assert.False(_host.Plugins.Single().Enabled);

        _host.Load([new PluginGrant { File = "a.js", Hash = new string('0', 64) }]);
        Assert.False(_host.Plugins.Single().Enabled);

        _host.Load([grant]);
        Assert.True(_host.Plugins.Single().Enabled);
        _host.Raise("completed", Record());
        await Until(() => Stored("a.js", "seen") is not null);
        Assert.Equal("big.iso:2147483648:x", Stored("a.js", "seen"));
    }

    [Fact]
    public async Task PermissionsAreEnforced()
    {
        var faulted = new TaskCompletionSource<PluginHost.Plugin>();
        _host.Faulted += p => faulted.TrySetResult(p);
        var grant = Write("b.js", Header("B") + "fluxer.on('failed', d => fluxer.notify('x', d.name));");
        _host.Load([grant]);
        _host.Raise("failed", Record());
        var p = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("\"notify\" permission", p.Error);
        Assert.Empty(_notes);

        var allowed = Write("c.js", Header("C", "notify") + "fluxer.on('failed', d => fluxer.notify('Failed', d.name));");
        _host.Load([allowed]);
        _host.Raise("failed", Record());
        await Until(() => { lock (_notes) return _notes.Count == 1; });
        Assert.Equal(("Failed", "big.iso"), _notes[0]);
    }

    [Fact]
    public async Task NoDotNet_AndRunawayScriptsAreStopped()
    {
        var grant = Write("d.js", Header("D") + """
            fluxer.store.set('types', [typeof System, typeof importNamespace, typeof require, typeof fetch].join(','));
            fluxer.on('added', () => { while (true) {} });
            """);
        var faulted = new TaskCompletionSource<PluginHost.Plugin>();
        _host.Faulted += p => faulted.TrySetResult(p);
        _host.Load([grant]);
        Assert.Equal("undefined,undefined,undefined,undefined", Stored("d.js", "types"));
        _host.Raise("added", Record());
        var p = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal("took too long", p.Error);
    }

    [Fact]
    public void ABrokenScript_ShowsItsError_AndStaysOff()
    {
        var grant = Write("e.js", Header("E") + "fluxer.on('nope', () => 1);");
        _host.Load([grant]);
        var p = _host.Plugins.Single();
        Assert.False(p.Enabled);
        Assert.Contains("unknown event", p.Error);
    }

    [Fact]
    public void Downloads_PermissionAddsToTheList()
    {
        var grant = Write("f.js", Header("F", "downloads") + "fluxer.store.set('id', String(fluxer.add('http://127.0.0.1:9/f.bin', { name: '../f.bin' })));");
        _host.Load([grant]);
        var d = _engine.Queue.Items.Single();
        Assert.Equal(d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Stored("f.js", "id"));
        Assert.Equal("f.bin", d.FileName);
    }

    [Fact]
    public void Example_IsAValidPlugin()
    {
        var path = _host.WriteExample();
        var m = PluginHost.ParseManifest(File.ReadAllText(path), Path.GetFileName(path))!;
        Assert.Equal(["notify"], m.Permissions);
        _host.Load([new PluginGrant { File = Path.GetFileName(path), Hash = PluginHost.HashOf(File.ReadAllBytes(path)) }]);
        Assert.True(_host.Plugins.Single().Enabled, _host.Plugins.Single().Error);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Fluxer.Core.Remote;
using Serilog;

namespace Fluxer.App.Services;

/// Settings → Automation → Remote access: the REST API, aria2 JSON-RPC and web page (Fluxer.Core.Remote).
public sealed partial class AppHost
{
    private RemoteApi? _remote;
    private (bool On, int Port, bool Lan, string? Key) _remoteApplied;

    /// "Listening on 127.0.0.1:6800" / "Port 6800 is in use" / "" when off.
    public string RemoteStatus { get; private set; } = "";
    public int RemotePort => _remote?.Port ?? 0;

    /// The key, made on first use and kept encrypted in the settings.
    public string RemoteKey()
    {
        var r = Engine.Settings.Remote;
        if (r.Key is { Length: > 0 } key) return key;
        key = RemoteApi.NewKey();
        r.Key = key;
        _ = Engine.SaveSettingsAsync(Engine.Settings);
        return key;
    }

    /// Starts, restarts or stops the server to match the settings; nothing happens when they did not change.
    public async Task ApplyRemoteAsync()
    {
        var r = Engine.Settings.Remote;
        var want = (r.On, r.Port, r.Lan, r.On ? RemoteKey() : null);
        if (want == _remoteApplied && (_remote is not null || !r.On)) return;
        _remoteApplied = want;
        if (_remote is not null)
        {
            await _remote.DisposeAsync();
            _remote = null;
        }
        RemoteStatus = "";
        if (!r.On) return;
        var api = new RemoteApi(Engine, want.Item4!, r.Lan, AppVersion);
        try
        {
            api.Start(r.Port);
            _remote = api;
            RemoteStatus = string.Format(CultureInfo.CurrentCulture, Strings.RemoteListening, (r.Lan ? "0.0.0.0" : "127.0.0.1") + ":" + api.Port);
            Log.Information("Remote access on port {Port} (network: {Lan})", api.Port, r.Lan);
        }
        catch (SocketException e)
        {
            RemoteStatus = string.Format(CultureInfo.CurrentCulture, Strings.RemotePortBusy, r.Port);
            Log.Warning("Remote access could not listen on {Port}: {Error}", r.Port, e.Message);
        }
    }

    /// Opens the web page with the key in the address fragment, which the browser never sends anywhere.
    public void OpenRemotePage()
    {
        if (_remote is null) return;
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_remote.Port}/#key={RemoteKey()}") { UseShellExecute = true })?.Dispose();
    }

    public async Task NewRemoteKeyAsync()
    {
        Engine.Settings.Remote.Key = RemoteApi.NewKey();
        await Engine.SaveSettingsAsync(Engine.Settings);
        await ApplyRemoteAsync();
    }

    private async Task StopRemoteAsync()
    {
        if (_remote is not null) await _remote.DisposeAsync();
        _remote = null;
    }
}

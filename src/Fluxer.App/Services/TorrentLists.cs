using System.Globalization;
using Serilog;

namespace Fluxer.App.Services;

/// Settings → Torrent: the subscribed tracker list (daily) and the peer block list (weekly when it is an address).
public sealed partial class AppHost
{
    private DateTime _lastListCheck;

    /// "182,544 ranges", "Could not read the list: …", or "" when there is none.
    public string IpFilterStatus { get; private set; } = "";

    private void RunTorrentLists()
    {
        if (DateTime.UtcNow - _lastListCheck < TimeSpan.FromHours(1)) return;
        _lastListCheck = DateTime.UtcNow;
        var s = Engine.Settings;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (s.TrackerListUrl is { Length: > 0 } && now - s.TrackerListAt > 24 * 3600) _ = RefreshTrackerListAsync();
        if (Uri.TryCreate(s.IpFilterSource, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && now - s.IpFilterAt > 7 * 24 * 3600)
            _ = RefreshIpFilterAsync(download: true);
    }

    public async Task<int?> RefreshTrackerListAsync()
    {
        try
        {
            var n = await Engine.RefreshTrackerListAsync();
            Log.Information("Tracker list: {Count} trackers", n);
            return n;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warning("Tracker list could not be fetched: {Error}", e.Message);
            return null;
        }
    }

    public async Task RefreshIpFilterAsync(bool download)
    {
        try
        {
            var n = await Engine.RefreshIpFilterAsync(download);
            IpFilterStatus = n > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.IpFilterLoaded, n)
                : Engine.Settings.IpFilterSource is not null ? Strings.IpFilterEmpty : "";
            if (n > 0) Log.Information("Peer block list: {Count} ranges", n);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            IpFilterStatus = string.Format(CultureInfo.CurrentCulture, Strings.IpFilterFailed, e.Message);
            Log.Warning("Peer block list: {Error}", e.Message);
        }
    }
}

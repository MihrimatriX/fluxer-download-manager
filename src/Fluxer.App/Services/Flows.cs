using System.Globalization;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Ipc;
using Serilog;

namespace Fluxer.App.Services;

/// User-facing flows that combine dialogs with engine calls.
public static class Flows
{
    public static async Task NewDownloadAsync(AppHost host, IReadOnlyList<string> urls, string? fileName, RequestContext? ctx, long? size = null)
    {
        var engine = host.Engine;
        var s = engine.Settings;

        // Browser captures can skip the dialog when the user turned it off.
        if (ctx is not null && !s.ShowAddDialog && urls.Count > 0)
        {
            foreach (var u in urls) engine.AddHttp(u, fileName, ctx);
            return;
        }

        var r = await Dialogs.NewDownloadAsync(host.Owner, s, urls, fileName, size);
        if (r is null) return;
        if (s.UseCategoryFolders != engine.Settings.UseCategoryFolders) await engine.SaveSettingsAsync(s);
        foreach (var url in r.Urls)
        {
            if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) { await TorrentAsync(host, null, url); continue; }
            var d = engine.AddHttp(url, r.FileName, ctx, r.Folder.Length > 0 ? r.Folder : null, r.Start, r.HashAlgorithm, r.Hash);
            if (r.Connections != s.ConnectionsPerDownload && d.Record.Http is { } h)
            {
                h.MaxConnections = r.Connections;
                engine.Db.Update(d.Record);
            }
        }
    }

    /// Multi-line paste/drop: http(s) and magnet links, one per line (or space separated).
    public static Task AddTextAsync(AppHost host, string text)
    {
        var urls = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Dialogs.IsDownloadable).Distinct().ToList();
        return urls.Count == 0 ? Task.CompletedTask : NewDownloadAsync(host, urls, null, null);
    }

    public static async Task TorrentAsync(AppHost host, byte[]? torrentFile, string? magnet)
    {
        string name;
        List<(string, long)> files = [];
        try
        {
            if (torrentFile is not null)
            {
                var t = MonoTorrent.Torrent.Load(torrentFile);
                name = t.Name;
                files = [.. t.Files.Select(f => (f.Path.ToString(), f.Length))];
            }
            else name = MonoTorrent.MagnetLink.Parse(magnet!).Name ?? magnet!;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Invalid torrent");
            await Dialogs.MessageAsync(host.Owner, Strings.AddTorrent, Strings.InvalidTorrent);
            return;
        }

        var choice = await Dialogs.TorrentAsync(host.Owner, host.Engine.Settings, name, files);
        if (choice is null) return;
        var added = host.Engine.AddTorrent(torrentFile, magnet, choice.Deselected, choice.Sequential, choice.Folder);
        if (added is null) await Dialogs.MessageAsync(host.Owner, Strings.AddTorrent, Strings.TorrentDuplicate);
    }

    /// Manual paste or a matched browser download. Rejects size mismatches, warns on changed validators.
    public static async Task<bool> RefreshLinkAsync(AppHost host, HttpDownload d, string url, RequestContext? ctx)
    {
        if (host.Engine.Queue.IsRunning(d.Id)) await host.Engine.Queue.PauseAsync(d.Id);
        LinkCheck check;
        try
        {
            check = await Dialogs.BusyAsync(host.Owner, Strings.RefreshLinkTitle, Strings.CheckingLink,
                _ => d.CheckNewLinkAsync(url, ctx, CancellationToken.None));
        }
        catch (Exception e)
        {
            await Dialogs.MessageAsync(host.Owner, Strings.RefreshLinkTitle, $"{Strings.LinkUnreachable}\n{e.Message}");
            return false;
        }

        if (!check.Reachable) { await Dialogs.MessageAsync(host.Owner, Strings.RefreshLinkTitle, Strings.LinkUnreachable); return false; }
        if (!check.SizeMatches) { await Dialogs.MessageAsync(host.Owner, Strings.RefreshLinkTitle, Strings.LinkSizeMismatch); return false; }
        if (check.ValidatorChanged && !await Dialogs.ConfirmAsync(host.Owner, Strings.RefreshLinkTitle, Strings.LinkValidatorChanged, Strings.ContinueAnyway))
            return false;

        d.ApplyNewLink(url, ctx, check);
        host.Engine.Queue.Resume(d.Id);
        return true;
    }

    // ================= media (yt-dlp) =================

    public static async Task MediaAsync(AppHost host, string pageUrl, CookieDto[]? cookies)
    {
        var engine = host.Engine;
        if (!await EnsureMediaReadyAsync(host)) return;

        var netscape = AppHost.NetscapeCookies(cookies);
        MediaProbe probe;
        try
        {
            probe = await Dialogs.BusyAsync(host.Owner, Strings.MediaTitle, Strings.MediaProbing, async _ =>
            {
                string? file = null;
                try
                {
                    if (netscape is not null) file = Cookies.WritePrivateFile(Path.Combine(Program.DataDirectory, "tmp"), netscape);
                    return await YtDlp.ProbeAsync(engine.Tools, pageUrl, file, CancellationToken.None);
                }
                finally { if (file is not null) File.Delete(file); }
            });
        }
        catch (MediaException e)
        {
            await Dialogs.MessageAsync(host.Owner, Strings.MediaTitle, Loc.Error(e.Code));
            return;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Probe failed");
            await Dialogs.MessageAsync(host.Owner, Strings.MediaTitle, Loc.Error("media-failed"));
            return;
        }

        var choice = await Dialogs.MediaAsync(host.Owner, engine.Settings, probe);
        if (choice is null) return;
        var format = YtDlp.FormatFor(choice.MaxHeight, choice.AudioOnly);
        foreach (var entry in choice.Entries)
            engine.AddMedia(entry.Url, entry.Title, format, choice.AudioOnly, netscape, choice.Folder);
        host.ShowMainWindow();
    }

    /// First use: one-time disclaimer, then consent + download of yt-dlp/deno/ffmpeg with hash checks.
    private static async Task<bool> EnsureMediaReadyAsync(AppHost host)
    {
        var engine = host.Engine;
        var s = engine.Settings;
        if (!s.MediaDisclaimerAccepted)
        {
            if (!await Dialogs.ConfirmAsync(host.Owner, Strings.MediaTitle, Strings.MediaDisclaimer, Strings.Accept)) return false;
            s.MediaDisclaimerAccepted = true;
            await engine.SaveSettingsAsync(s);
        }
        if (engine.Tools.IsInstalled) return true;
        if (!await Dialogs.ConfirmAsync(host.Owner, Strings.ToolsTitle, Strings.ToolsConsent, Strings.Download)) return false;

        try
        {
            await Dialogs.BusyAsync(host.Owner, Strings.ToolsTitle, Strings.ToolsDownloading, async progress =>
            {
                void OnProgress(string tool, double f) => progress.Report($"{tool}: {f * 100:F0}%");
                engine.Tools.Progress += OnProgress;
                try { await engine.Tools.InstallAsync(CancellationToken.None); }
                finally { engine.Tools.Progress -= OnProgress; }
                return true;
            });
            s.LastYtDlpCheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await engine.SaveSettingsAsync(s);
            return true;
        }
        catch (ToolVerificationException e)
        {
            Log.Error("Tool hash mismatch: {Tool}", e.Tool);
            await Dialogs.MessageAsync(host.Owner, Strings.ToolsTitle, Strings.ErrToolHash);
        }
        catch (Exception e)
        {
            Log.Error(e, "Tool install failed");
            await Dialogs.MessageAsync(host.Owner, Strings.ToolsTitle, $"{Strings.ToolsFailed}\n{e.Message}");
        }
        return false;
    }

    // ================= page grabber =================

    public static async Task GrabAsync(AppHost host, PageResources page)
    {
        if (page.Resources.Length == 0)
        {
            await Dialogs.MessageAsync(host.Owner, Strings.GrabTitle, Strings.GrabEmpty);
            return;
        }
        var ctx = new RequestContext(page.PageUrl, page.UserAgent, AppHost.CookieHeader(page.Cookies));
        var result = await Dialogs.GrabAsync(host.Owner, host.Engine.Settings, page, host.Engine.Http, ctx);
        if (result is not { } r) return;
        foreach (var url in r.Urls) host.Engine.AddHttp(url, null, ctx, r.Folder);
        host.ShowMainWindow();
        Log.Information("Grabbed {Count} resources from {Page}", r.Urls.Count, new Uri(page.PageUrl).Host);
    }
}

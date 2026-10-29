using System.Globalization;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Platform.Windows;
using Fluxer.Ipc;
using Serilog;

namespace Fluxer.App.Services;

/// User-facing flows that combine dialogs with engine calls.
public static class Flows
{
    /// `ctx`: the browser's context (null for typed or pasted links).
    public static Task NewDownloadAsync(AppHost host, IReadOnlyList<string> urls, RequestContext? ctx) =>
        NewDownloadAsync(host, [.. urls.Select(u => new ViewModels.Captured(u, Context: ctx))], ctx);

    /// The add window that browser links are joining (UI thread only).
    private static ViewModels.AddDownloadViewModel? _browserAdd;

    /// `night`: the browser asked for the night queue (preselected in the dialog).
    public static async Task NewDownloadAsync(AppHost host, IReadOnlyList<ViewModels.Captured> links, RequestContext? ctx, bool night = false)
    {
        var engine = host.Engine;
        var s = engine.Settings;

        // One link that is already in the list: ask before making a second copy.
        if (links.Count == 1 && !UrlPattern.Has(links[0].Url) && engine.Queue.FindByUrl(links[0].Url) is { } existing)
        {
            var canUpdate = existing is HttpDownload && existing.State != DownloadState.Completed;
            switch (await Dialogs.DuplicateAsync(host.Owner, existing, canUpdate))
            {
                case DuplicateChoice.Skip: return;
                case DuplicateChoice.Show:
                    host.ShowMainWindow();
                    host.Main.Reveal(existing);
                    return;
                case DuplicateChoice.Update:
                    await RefreshLinkAsync(host, (HttpDownload)existing, links[0].Url, links[0].Context ?? ctx);
                    return;
            }
        }

        // Browser captures can skip the dialog when the user turned it off.
        if (ctx is not null && !s.ShowAddDialog && links.Count > 0)
        {
            // Without the window there is nobody to ask: a link already listed (several at once) is skipped.
            foreach (var l in links.Where(l => links.Count == 1 || engine.Queue.FindByUrl(l.Url) is null))
            {
                var d = engine.AddHttp(l.Url, l.FileName ?? l.Probe?.FileName, l.Context ?? ctx, rejectHtml: true, night: night);
                if (!night) host.OpenProgressIfEnabled(d, activate: false);
            }
            return;
        }

        // A site's "download all" button starts many browser downloads at once: they join the window the
        // first one opened instead of each opening its own.
        if (ctx is not null && !night && _browserAdd is { } open)
        {
            open.AddLinks(links);
            return;
        }

        var vm = new ViewModels.AddDownloadViewModel(s, engine.Http, links, ctx) { QueueIndex = night ? 1 : 0, Existing = engine.Queue.FindByUrl };
        // Any open window collects them: pasted links that needed a sign-in come back here from "Start in browser".
        if (!night) _browserAdd ??= vm;
        ViewModels.AddResult? r;
        try { r = await AddDownloadWindow.ShowAsync(host.Owner, vm); }
        finally { if (_browserAdd == vm) _browserAdd = null; }
        if (r is null) return;
        if (vm.DontAskAgain && s.ShowAddDialog)
        {
            s.ShowAddDialog = false;
            await engine.SaveSettingsAsync(s);
        }

        // Remember the folder (most recent first) unless it is the default or a category folder of it.
        var isDefault = r.Folder.StartsWith(s.DownloadDirectory, StringComparison.OrdinalIgnoreCase)
                        && Path.GetDirectoryName(r.Folder.TrimEnd('\\')) is { } parent
                        && (string.Equals(r.Folder.TrimEnd('\\'), s.DownloadDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(parent.TrimEnd('\\'), s.DownloadDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!isDefault)
        {
            s.RecentFolders = [r.Folder, .. s.RecentFolders.Where(f => !string.Equals(f, r.Folder, StringComparison.OrdinalIgnoreCase)).Take(5)];
            await engine.SaveSettingsAsync(s);
        }

        var added = new List<IDownload>();
        foreach (var item in r.Items)
        {
            if (item.IsMagnet) { await TorrentAsync(host, null, item.Url); continue; }
            if (item.Kind == ProbeKind.Torrent || Path.GetExtension(new Uri(item.Url).AbsolutePath).Equals(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                await TorrentFromUrlAsync(host, item.Url, item.Context);
                continue;
            }
            // Several files with "automatic" category: each goes to its own category folder.
            var folder = r.FolderIsPerFile && item.Category is { } cat ? s.CategoryFolder(cat)
                : r.FolderIsPerFile ? s.DownloadDirectory : r.Folder;
            added.Add(engine.AddHttp(item.Url, item.UserNamed ? item.Name : null, item.Context, folder, r.Mode != ViewModels.StartMode.Paused,
                // Only a link the user kept knowing it is a web page may be saved as one; any other page
                // (sign-in, expired session) fails with "got-html" instead of finishing as a 1 KB "download.php".
                r.HashAlgorithm, r.Hash, rejectHtml: item.Kind != ProbeKind.Html, connections: r.Connections,
                night: r.Mode == ViewModels.StartMode.Night, mirrors: r.Mirrors, category: r.Category ?? item.Category, tags: r.Tags));
        }

        // "Start now" jumps the queue; "Add to queue" waits its turn at the end.
        if (r.Mode == ViewModels.StartMode.Now && added.Count > 0)
            engine.Queue.Reorder([.. added.Select(d => d.Id)], DownloadQueue.Move.Top);
        if (added.Count == 1 && r.Mode is ViewModels.StartMode.Now or ViewModels.StartMode.Queue) host.OpenProgressIfEnabled(added[0], activate: true);
    }

    /// A .torrent link: fetch it (small) and open the torrent dialog instead of saving the file.
    public static async Task TorrentFromUrlAsync(AppHost host, string url, RequestContext? ctx)
    {
        byte[]? bytes = null;
        try
        {
            bytes = await Dialogs.BusyAsync(host.Owner, Strings.AddTorrent, Strings.Probing,
                _ => HttpProbe.FetchTorrentAsync(host.Engine.Http, url, ctx, CancellationToken.None));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            Log.Warning("Fetching torrent failed: {Error}", e.Message);
        }
        if (bytes is null) await Dialogs.MessageAsync(host.Owner, Strings.AddTorrent, Strings.TorrentFetchFailed);
        else await TorrentAsync(host, bytes, null);
    }

    /// Multi-line paste/drop: http(s) and magnet links, one per line (or space separated).
    public static Task AddTextAsync(AppHost host, string text)
    {
        var urls = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Dialogs.IsDownloadable).Distinct().ToList();
        return urls.Count == 0 ? Task.CompletedTask : NewDownloadAsync(host, urls, null);
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
        else
        {
            host.OpenProgressIfEnabled(added, activate: true);
            await OfferFirewallAsync(host);
        }
    }

    // ================= firewall (torrent peers connecting to us) =================

    private static string FirewallRuleName => $"{Path.GetFileName(PortableData.LocalDirectory)} (torrent)";
    public static bool FirewallOpen() => FirewallRule.Exists(FirewallRuleName);
    public static bool AllowFirewall() => FirewallRule.TryAdd(FirewallRuleName, Environment.ProcessPath!);

    /// Asked once, at the first torrent; Settings → Torrent has the same button for later.
    private static async Task OfferFirewallAsync(AppHost host)
    {
        var s = host.Engine.Settings;
        if (s.FirewallAsked || await Task.Run(FirewallOpen)) return;
        s.FirewallAsked = true;
        await host.Engine.SaveSettingsAsync(s);
        if (await Dialogs.ConfirmAsync(host.Owner, Strings.FirewallTitle, Strings.FirewallBody, Strings.FirewallAllow))
            await Task.Run(AllowFirewall);
    }

    /// Manual paste or a matched browser download. Rejects size mismatches. When the partial file
    /// cannot be continued from the new link (changed validators, no range support), offers a restart.
    public static async Task<bool> RefreshLinkAsync(AppHost host, HttpDownload d, string url, RequestContext? ctx)
    {
        var queue = host.Engine.Queue;
        var wasRunning = queue.IsRunning(d.Id);
        if (wasRunning) await queue.PauseAsync(d.Id);

        var check = await CheckAsync();
        if (check is null)
        {
            if (wasRunning) queue.Resume(d.Id);   // the old link was still working
            return false;
        }

        // Resuming would splice two versions of the file together, or the new server cannot continue
        // from the middle: the only safe way forward is from byte 0.
        if (check.ValidatorChanged || (d.Record.DoneBytes > 0 && !check.SupportsRanges))
        {
            var message = check.ValidatorChanged ? Strings.LinkValidatorChanged : Strings.LinkNoResume;
            if (!await Dialogs.ConfirmAsync(host.Owner, Strings.RefreshLinkTitle, message, Strings.RestartFromScratch))
            {
                if (wasRunning) queue.Resume(d.Id);
                return false;
            }
            if (queue.IsRunning(d.Id)) await queue.PauseAsync(d.Id);   // resumed while the dialog was open
            d.ApplyNewLink(url, ctx, check);
            d.ResetForRestart();
        }
        else
        {
            if (queue.IsRunning(d.Id)) await queue.PauseAsync(d.Id);
            d.ApplyNewLink(url, ctx, check);
        }
        queue.Resume(d.Id);
        return true;

        async Task<LinkCheck?> CheckAsync()
        {
            LinkCheck c;
            try
            {
                c = await Dialogs.BusyAsync(host.Owner, Strings.RefreshLinkTitle, Strings.CheckingLink,
                    _ => d.CheckNewLinkAsync(url, ctx, CancellationToken.None));
            }
            catch (Exception e)
            {
                await Dialogs.MessageAsync(host.Owner, Strings.RefreshLinkTitle, $"{Strings.LinkUnreachable}\n{e.Message}");
                return null;
            }
            if (!c.Reachable) { await Dialogs.MessageAsync(host.Owner, Strings.RefreshLinkTitle, Strings.LinkUnreachable); return null; }
            if (!c.SizeMatches) { await Dialogs.MessageAsync(host.Owner, Strings.RefreshLinkTitle, Strings.LinkSizeMismatch); return null; }
            return c;
        }
    }

    // ================= media (yt-dlp) =================

    public static async Task MediaAsync(AppHost host, string pageUrl, CookieDto[]? cookies, string? referrer = null)
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
                    return await YtDlp.ProbeAsync(engine.Tools, pageUrl, file, CancellationToken.None, referrer);
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
        var media = choice.Entries.Select(entry => engine.AddMedia(entry.Url, entry.Title, format, choice.AudioOnly, netscape, choice.Folder, referrer)).ToList();
        if (media.Count == 1) host.OpenProgressIfEnabled(media[0], activate: true);
        else host.ShowMainWindow();
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
            await Dialogs.BusyAsync(host.Owner, Strings.ToolsTitle, Strings.ToolsDownloading, async (progress, ct) =>
            {
                void OnProgress(string tool, double f) => progress.Report($"{tool}: {f * 100:F0}%");
                engine.Tools.Progress += OnProgress;
                try { await engine.Tools.InstallAsync(ct); }
                finally { engine.Tools.Progress -= OnProgress; }
                return true;
            });
            s.LastYtDlpCheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await engine.SaveSettingsAsync(s);
            return true;
        }
        catch (OperationCanceledException)
        {
            // The user cancelled; tools finished before that stay installed, the rest was cleaned up.
        }
        catch (ToolVerificationException e)
        {
            Log.Error("Tool hash mismatch: {Tool}", e.Tool);
            await Dialogs.MessageAsync(host.Owner, Strings.ToolsTitle, Strings.ErrToolHash);
        }
        catch (ToolStalledException e)
        {
            Log.Warning("Tool download stalled: {Tool}", e.Tool);
            await Dialogs.MessageAsync(host.Owner, Strings.ToolsTitle, Strings.ErrToolStalled);
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

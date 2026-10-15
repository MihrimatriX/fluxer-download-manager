using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Fluxer.App.ViewModels;
using Fluxer.App.Views;
using Fluxer.Core;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Platform.Windows;
using Fluxer.Ipc;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Fluxer.App.Services;

/// Owns the engine, tray, pipe server and window, and runs the flows that need user input
/// (browser downloads, link matching, torrents, media, page grabbing).
public sealed partial class AppHost(App app, IClassicDesktopStyleApplicationLifetime desktop)
{
    public static AppHost Current { get; private set; } = null!;

    private TrayIcon? _tray;
    private TrayWindow? _trayWindow;
    private PipeServer? _pipe;
    private DispatcherTimer? _timer;
    private bool _exiting;
    private string _lastScheduleFire = "";
    private UpdateInfo? _pendingUpdate;
    private UpdateManager? _updates;

    public FluxerEngine Engine { get; private set; } = null!;
    public MainViewModel Main { get; private set; } = null!;
    public MainWindow? Window { get; private set; }
    public Window? Owner => Window is { IsVisible: true } w ? w : null;

    public void Start(string[] args)
    {
        Current = this;
        var sw = Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(string step) { marks.Add($"{step} {sw.ElapsedMilliseconds}"); sw.Restart(); }
        Mark($"runtime+avalonia {(int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds}");
        Engine = new FluxerEngine(Program.DataDirectory);
        if (Engine.Settings.UpgradeDesign()) Engine.Db.SetSetting("app", Engine.Settings);
        Engine.Settings.UseProfile(Engine.Settings.ActiveProfile); // limits always match the active profile
        _ = Engine.SaveSettingsAsync(Engine.Settings);
        Mark("engine");
        Loc.Apply(Engine.Settings.Language);
        ApplyTheme();
        Appearance.Apply(app, Engine.Settings.Ui);
        _ = SetCurrentProcessExplicitAppUserModelID(ShellIntegration.Aumid);
        RegisterIntegration();
        MediaDownload.CleanupTemp(Engine.Tools);
        Mark("theme+integration");

        Main = new MainViewModel(this);
        Engine.Queue.Added += d => Dispatcher.UIThread.Post(() => Main.OnAdded(d));
        Engine.Queue.Removed += d => Dispatcher.UIThread.Post(() => { Main.OnRemoved(d); CloseDownloadWindows(d.Id); });
        Engine.Queue.StateChanged += OnStateChanged;
        Engine.Queue.Reordered += () => Dispatcher.UIThread.Post(Main.OnReordered);
        Engine.Queue.Drained += () => Dispatcher.UIThread.Post(OnDrained);
        Engine.Queue.Load();
        Mark("queue");
        Main.Reload();
        Mark("viewmodels");

        _pipe = new PipeServer(HandleMessageAsync);
        _pipe.Start();
        BuildTray();
        Mark("pipe+tray");

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();

        _ = CheckToolUpdateAsync();
        _ = CheckAppUpdateAsync();

        if (!args.Contains("--background")) ShowMainWindow();
        Mark("window");
        Log.Information("Startup (ms): {Marks}", string.Join(", ", marks));
        _ = HandleArgsAsync([.. args.Where(a => a != "--background")]);
    }

    // ================= window / tray / lifetime =================

    public void ShowMainWindow()
    {
        if (Window is null)
        {
            Window = new MainWindow { DataContext = Main };
            Appearance.ApplyTo(Window, Engine.Settings.Ui);
            Window.RestoreLayout(Engine.Settings.Ui);
            Window.Opened += (_, _) => Log.Information("Main window ready {Ms} ms after process start",
                (int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
            Window.Closing += (_, e) =>
            {
                SaveLayout();
                if (_exiting) return;
                e.Cancel = true;       // close = minimize to tray; exit only from the tray menu
                Window.Hide();
            };
        }
        Window.Show();
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Activate();
    }

    /// Language/theme change: rebuild every surface that captured strings at construction.
    public void ReloadUi()
    {
        Loc.Apply(Engine.Settings.Language);
        ApplyTheme();
        BuildTray();
        _trayWindow?.Close(); // captured strings; rebuilt on the next click
        _trayWindow = null;
        Main.RefreshTexts();
        if (Window is not { } old) return;
        var wasVisible = old.IsVisible;
        Window = null;
        _exiting = true;
        old.Close();
        _exiting = false;
        if (wasVisible) ShowMainWindow();
    }

    private void ApplyTheme() => app.RequestedThemeVariant = Engine.Settings.Theme switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// Left click on the tray icon: the small status window (right click keeps the menu).
    private void ToggleTrayWindow()
    {
        if (_trayWindow is null)
        {
            _trayWindow = new TrayWindow { DataContext = Main };
            _trayWindow.Classes.Set("motion", Engine.Settings.Ui.Animations);
        }
        Main.UpdateTotals();
        _trayWindow.Toggle();
    }

    private void BuildTray()
    {
        _tray?.Dispose();
        var menu = new NativeMenu();
        menu.Add(Item(Strings.TrayOpen, ShowMainWindow));
        menu.Add(Item(Strings.AddUrl, () => _ = Main.AddUrlCommand.ExecuteAsync(null)));
        menu.Add(Item(Strings.Settings, () => { ShowMainWindow(); Main.OpenSettingsCommand.Execute(null); }));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Strings.PauseAll, () => _ = Engine.Queue.PauseAllAsync()));
        menu.Add(Item(Strings.ResumeAll, Engine.Queue.ResumeAll));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Strings.TrayExit, () => _ = ExitAsync()));

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Fluxer/Assets/fluxer.ico"))),
            ToolTipText = "Fluxer",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ToggleTrayWindow();
        TrayIcon.SetIcons(app, [_tray]);

        static NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// Window size/position, columns and panel sizes are remembered.
    public void SaveLayout()
    {
        if (Window is null) return;
        Window.CaptureLayout(Engine.Settings.Ui);
        _ = Engine.SaveSettingsAsync(Engine.Settings);
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        SaveLayout();
        _exiting = true;
        _timer?.Stop();
        await Main.CommitPendingRemovalsAsync();
        Window?.Close();
        _pipe?.Dispose();
        await Engine.DisposeAsync();  // pauses everything → final checkpoints
        _tray?.Dispose();
        if (_pendingUpdate is not null && _updates is not null)
        {
            try { _updates.WaitExitThenApplyUpdates(_pendingUpdate.TargetFullRelease, silent: true, restart: false); }
            catch (Exception e) { Log.Warning(e, "Applying update failed"); }
        }
        desktop.Shutdown();
    }

    private void Tick()
    {
        var items = Engine.Queue.Items;
        var active = items.Where(d => d.State is DownloadState.Downloading or DownloadState.Seeding).ToList();
        var speed = active.Sum(d => d.GetProgress().BytesPerSecond);
        if (_tray is not null)
            _tray.ToolTipText = active.Count == 0 ? "Fluxer" : $"Fluxer — {active.Count} · {Loc.Speed(speed)}";
        Main.UpdateTotals();
        RunScheduler();
        RunWeekSchedule();
    }

    private void RunScheduler()
    {
        var s = Engine.Settings;
        var now = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var key = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (key == _lastScheduleFire) return;
        if (s.ScheduleStart == now) { _lastScheduleFire = key; Engine.Queue.StartNight(); Log.Information("Scheduler: night queue start"); }
        else if (s.ScheduleStop == now) { _lastScheduleFire = key; _ = Engine.Queue.PauseAllAsync(); Log.Information("Scheduler: stop"); }
    }

    private int? _lastScheduledProfile;

    /// The weekly grid switches profiles only when its cell changes, so a profile picked by hand from the
    /// status bar holds until the next change in the grid.
    private void RunWeekSchedule()
    {
        var want = Engine.Settings.ScheduledProfile(DateTime.Now);
        if (want is null) { _lastScheduledProfile = null; return; }
        if (want == _lastScheduledProfile) return;
        _lastScheduledProfile = want;
        if (want != Engine.Settings.ActiveProfile)
        {
            Log.Information("Schedule: profile {Profile}", want);
            _ = UseProfileAsync(want.Value);
        }
    }

    /// Status bar pill, weekly schedule, settings page.
    public async Task UseProfileAsync(int index)
    {
        var s = Engine.Settings;
        s.UseProfile(index);
        await Engine.SaveSettingsAsync(s);
        Main.UpdateProfileText();
        Main.UpdateTotals();
        Main.Settings.ProfileApplied();
    }

    private void OnDrained()
    {
        var s = Engine.Settings;
        if (s.AfterQueue == "None") return;
        var action = s.AfterQueue;
        s.AfterQueue = "None"; // one-shot: never surprise the user twice
        _ = Engine.SaveSettingsAsync(s);
        Log.Information("Queue drained → {Action}", action);
        if (action == "Shutdown") Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true });
        else if (action == "Sleep") Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { CreateNoWindow = true });
    }

    // ================= per-download windows =================

    private readonly Dictionary<long, DownloadWindow> _downloadWindows = [];
    private DownloadWindow? _card;

    /// Opens (or brings forward) the progress window of a download.
    public void OpenDownloadWindow(IDownload d, bool activate = true)
    {
        if (_downloadWindows.TryGetValue(d.Id, out var existing))
        {
            if (activate) existing.Activate();
            return;
        }
        if (_card?.DownloadId == d.Id) _card.Close();
        var w = new DownloadWindow(new DownloadWindowViewModel(this, d, card: false)) { ShowActivated = activate };
        Appearance.ApplyTo(w, Engine.Settings.Ui);
        w.Closed += (_, _) => _downloadWindows.Remove(d.Id);
        _downloadWindows[d.Id] = w;
        w.Show();
    }

    public void OpenProgressIfEnabled(IDownload d, bool activate)
    {
        if (Engine.Settings.ShowProgressWindow) OpenDownloadWindow(d, activate);
    }

    /// Small corner card when a download finishes without its progress window open. One at a time.
    private void ShowCompletedCard(IDownload d)
    {
        if (!Engine.Settings.ShowCompletedCard || _downloadWindows.ContainsKey(d.Id)) return;
        _card?.Close();
        var card = new DownloadWindow(new DownloadWindowViewModel(this, d, card: true)) { ShowActivated = false };
        Appearance.ApplyTo(card, Engine.Settings.Ui);
        card.Closed += (_, _) => { if (_card == card) _card = null; };
        _card = card;
        card.Show();
    }

    private void CloseDownloadWindows(long id)
    {
        if (_downloadWindows.TryGetValue(id, out var w)) w.Close();
        if (_card?.DownloadId == id) _card.Close();
    }

    private void OnStateChanged(IDownload d)
    {
        if (d.State == DownloadState.Completed) Dispatcher.UIThread.Post(() => ShowCompletedCard(d));
        Log.Information("Download {Id} ({Kind}) → {State} {Error}", d.Id, d.Kind, d.State, d.Record.Error);
        Dispatcher.UIThread.Post(() => Main.OnStateChanged(d));
        if (!Engine.Settings.Notifications) return;
        switch (d.State)
        {
            case DownloadState.Completed:
                Notifier.Show(Strings.NotifyCompleted, d.FileName);
                break;
            case DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh:
                Notifier.Show(Loc.State(d.State), $"{d.FileName}\n{Loc.Error(d.Record.Error)}");
                break;
        }
    }

    public void RegisterIntegration()
    {
        if (Protocol.Instance.Length > 0) return; // side-by-side dev profile never touches the registry
        try
        {
            var dir = AppContext.BaseDirectory;
            ShellIntegration.Register(Path.Combine(dir, "Fluxer.exe"), Path.Combine(dir, "Fluxer.NativeHost.exe"),
                Program.DataDirectory, Protocol.AllowedExtensionIds, Engine.Settings.RegisterFileAssociations);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Shell integration failed");
        }
    }

    // ================= updates =================

    private async Task CheckToolUpdateAsync()
    {
        var s = Engine.Settings;
        if (!Engine.Tools.IsInstalled || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.LastYtDlpCheck < 7 * 86400) return;
        try
        {
            await Engine.Tools.UpdateYtDlpAsync(CancellationToken.None);
            s.LastYtDlpCheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await Engine.SaveSettingsAsync(s);
        }
        catch (Exception e) { Log.Warning(e, "Weekly yt-dlp check failed"); }
    }

    private async Task CheckAppUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(Engine.Settings.UpdateFeed)) return;
        try
        {
            var feed = Engine.Settings.UpdateFeed!;
            _updates = feed.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                ? new UpdateManager(new GithubSource(feed, null, false))
                : new UpdateManager(feed);
            if (!_updates.IsInstalled) return;
            var info = await _updates.CheckForUpdatesAsync();
            if (info is null) return;
            await _updates.DownloadUpdatesAsync(info);
            _pendingUpdate = info;
            Notifier.Show(Strings.UpdateReadyTitle, string.Format(CultureInfo.CurrentCulture, Strings.UpdateReadyBody, info.TargetFullRelease.Version));
        }
        catch (Exception e) { Log.Warning(e, "App update check failed"); }
    }

    public bool HasPendingUpdate => _pendingUpdate is not null;

    public void RestartToUpdate()
    {
        if (_pendingUpdate is null || _updates is null) return;
        _ = Task.Run(async () =>
        {
            await Engine.Queue.StopAllAsync();
            _updates.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
        });
    }

    // ================= pipe messages =================

    private async Task<Envelope> HandleMessageAsync(Envelope msg)
    {
        if (msg.Type == MessageTypes.DownloadAdd && msg.PayloadAs<DownloadAdd>() is { } add)
            return await OnDownloadAddAsync(msg.Id, add);
        if (msg.Type == MessageTypes.Command && msg.PayloadAs<Command>() is { } cmd)
            return await Dispatcher.UIThread.InvokeAsync(() => RunCommandAsync(msg.Id, cmd));
        return HandleOther(msg);
    }

    private Envelope HandleOther(Envelope msg)
    {
        var accepted = Envelope.Create(msg.Id, MessageTypes.DownloadAddResult, new DownloadAddResult("accepted", null));
        switch (msg.Type)
        {
            case MessageTypes.Ping:
                return Envelope.Create(msg.Id, MessageTypes.Pong, new Pong(typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0"));
            case MessageTypes.StatusGet:
                return Envelope.Create(msg.Id, MessageTypes.Status, BuildStatus());
            case MessageTypes.PageSend when msg.PayloadAs<PageSend>() is { } page:
                // A captured stream (m3u8/mpd) comes with the page it plays on as referrer.
                var referrer = page.Referrer is { } r && r != page.PageUrl ? r : null;
                Dispatcher.UIThread.Post(() => _ = Guard(Flows.MediaAsync(this, page.PageUrl, page.Cookies, referrer)));
                return accepted;
            case MessageTypes.DownloadBatch when msg.PayloadAs<DownloadBatch>() is { } batch:
                var ctx = new RequestContext(batch.Referrer ?? batch.PageUrl, batch.UserAgent, CookieHeader(batch.Cookies));
                var urls = batch.Items.Select(i => i.Url).Distinct().ToList();
                Dispatcher.UIThread.Post(() => _ = Guard(Flows.NewDownloadAsync(this, urls, null, ctx)));
                return accepted;
            case MessageTypes.PageResources when msg.PayloadAs<PageResources>() is { } res:
                Dispatcher.UIThread.Post(() => _ = Guard(Flows.GrabAsync(this, res)));
                return accepted;
            case MessageTypes.InstanceArgs when msg.PayloadAs<InstanceArgs>() is { } ia:
                Dispatcher.UIThread.Post(() => _ = Guard(HandleArgsAsync(ia.Args, showWindow: true)));
                return accepted;
            default:
                return Envelope.Error(msg.Id, ErrorCodes.InvalidMessage, "unknown type");
        }
    }

    private StatusReply BuildStatus()
    {
        var items = Engine.Queue.Items;
        var active = items.Where(d => d.State is DownloadState.Starting or DownloadState.Downloading).Select(d => (d, p: d.GetProgress())).ToList();
        return new StatusReply(active.Count, items.Count(d => d.State == DownloadState.Queued), active.Sum(x => x.p.BytesPerSecond),
            [.. active.OrderByDescending(x => x.p.BytesPerSecond).Take(5).Select(x => new StatusItem(x.d.FileName, x.p.Fraction, x.p.BytesPerSecond))],
            [.. PopupList(items)]);
    }

    /// The popup's list: everything unfinished (running first), then the latest finished ones. Bounded so
    /// the reply stays small.
    private static IEnumerable<StatusEntry> PopupList(IReadOnlyList<IDownload> items)
    {
        static int Rank(DownloadState s) => s switch
        {
            DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying => 0,
            DownloadState.Seeding => 1,
            DownloadState.Queued or DownloadState.Scheduled => 2,
            DownloadState.Paused => 3,
            DownloadState.Completed => 5,
            _ => 4,
        };
        static string Name(DownloadState s) => s switch
        {
            DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying => "downloading",
            DownloadState.Seeding => "seeding",
            DownloadState.Queued or DownloadState.Scheduled => "queued",
            DownloadState.Paused => "paused",
            DownloadState.Completed => "completed",
            _ => "failed",
        };
        var open = items.Where(d => d.State != DownloadState.Completed).OrderBy(d => Rank(d.State)).Take(20);
        var done = items.Where(d => d.State == DownloadState.Completed).OrderByDescending(d => d.Record.CreatedAt).Take(8);
        foreach (var d in open.Concat(done))
        {
            var p = d.GetProgress();
            yield return new StatusEntry(d.Id, d.FileName.Length > 0 ? d.FileName : d.Record.Http?.Url ?? "…", Name(d.State),
                d.Kind.ToString().ToLowerInvariant(), d.State == DownloadState.Completed ? 1 : p.Fraction, p.BytesPerSecond, p.BytesDone, p.TotalBytes);
        }
    }

    /// Popup buttons. Replies "accepted" or an error the popup can show.
    private async Task<Envelope> RunCommandAsync(string id, Command c)
    {
        var ok = Envelope.Create(id, MessageTypes.DownloadAddResult, new DownloadAddResult("accepted", null));
        var q = Engine.Queue;
        switch (c.Action)
        {
            case CommandActions.PauseAll: await q.PauseAllAsync(); return ok;
            case CommandActions.ResumeAll: q.ResumeAll(); return ok;
            case CommandActions.Show: ShowMainWindow(); return ok;
        }
        if (q.Items.FirstOrDefault(d => d.Id == c.Id) is not { } dl)
            return Envelope.Error(id, ErrorCodes.InvalidMessage, "unknown download");
        switch (c.Action)
        {
            case CommandActions.Pause: await q.PauseAsync(dl.Id); break;
            case CommandActions.Resume: q.Resume(dl.Id); break;
            case CommandActions.Remove: await q.RemoveAsync(dl.Id, deleteFiles: false); break;
            case CommandActions.Open: ViewModels.MainViewModel.OpenFileOf(dl); break;
            case CommandActions.Folder: ViewModels.MainViewModel.ShowInFolder(dl); break;
            case CommandActions.Details: OpenDownloadWindow(dl); break;
            default: return Envelope.Error(id, ErrorCodes.InvalidMessage, "unknown action");
        }
        return ok;
    }

    private static async Task Guard(Task t)
    {
        try { await t; }
        catch (Exception e) { Log.Error(e, "UI flow failed"); }
    }

    public static string? CookieHeader(CookieDto[]? cookies) =>
        cookies is { Length: > 0 } ? Cookies.ToHeader(cookies.Select(ToEntry)) : null;

    public static string? NetscapeCookies(CookieDto[]? cookies) =>
        cookies is { Length: > 0 } ? Cookies.ToNetscape(cookies.Select(ToEntry)) : null;

    private static CookieEntry ToEntry(CookieDto c) => new(c.Name, c.Value, c.Domain, c.Path, c.Secure, c.HttpOnly, c.ExpirationDate);

    // ================= flows =================

    public async Task HandleArgsAsync(string[] args, bool showWindow = false)
    {
        var handled = false;
        foreach (var a in args)
        {
            if (a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) { handled = true; await Flows.TorrentAsync(this, null, a); }
            else if (a.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(a)) { handled = true; await Flows.TorrentAsync(this, await File.ReadAllBytesAsync(a), null); }
            else if (Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") { handled = true; await Flows.NewDownloadAsync(this, [a], null, null); }
        }
        if (showWindow && !handled) ShowMainWindow();
    }

    /// Answers the extension synchronously: "accepted" (Fluxer takes it, the browser cancels its copy)
    /// or "rejected" with a reason (the browser resumes its paused download). Before accepting we
    /// probe the URL ourselves — if we would only get a web page (Cloudflare, one-time link, missing
    /// session), the browser is the one that can download it.
    private async Task<Envelope> OnDownloadAddAsync(string id, DownloadAdd add)
    {
        Envelope Reply(string status, string? reason = null) =>
            Envelope.Create(id, MessageTypes.DownloadAddResult, new DownloadAddResult(status, reason));

        if (add.TorrentPath is { } path)
        {
            var bytes = await ReadTorrentFileAsync(path);
            Log.Information("Browser saved torrent {File}: {Valid}", Path.GetFileName(path), bytes is not null);
            Dispatcher.UIThread.Post(() => _ = Guard(bytes is not null
                ? Flows.TorrentAsync(this, bytes, null)
                : Dialogs.MessageAsync(Owner, Strings.AddTorrent, Strings.InvalidTorrentFile)));
            return Reply(bytes is not null ? "accepted" : "rejected", bytes is null ? "invalid-torrent" : null);
        }

        if (add.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => _ = Guard(Flows.TorrentAsync(this, null, add.Url)));
            return Reply("accepted");
        }

        var ctx = new RequestContext(add.Referrer, add.UserAgent, CookieHeader(add.Cookies));
        var (probe, workingCtx) = await HttpProbe.RunWithFallbackAsync(Engine.Http, add.Url, ctx, CancellationToken.None);
        ctx = workingCtx ?? ctx;
        var host = Uri.TryCreate(add.Url, UriKind.Absolute, out var u) ? u.Host : "?";
        Log.Information("Browser {Source} from {Host}: probe {Kind} HTTP {Status} {Mime}", add.Source, host, probe.Kind, probe.Status, probe.MediaType);

        var fromMenu = add.Source == "context-menu";
        var expectsHtml = FileNameResolver.IsHtml(add.Mime)
                          || add.FileName?.EndsWith(".html", StringComparison.OrdinalIgnoreCase) == true
                          || add.FileName?.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) == true;
        switch (probe.Kind)
        {
            case ProbeKind.Torrent when fromMenu:
                Dispatcher.UIThread.Post(() => _ = Guard(Flows.TorrentFromUrlAsync(this, add.Url, ctx)));
                return Reply("accepted");
            case ProbeKind.Torrent:
                return Reply("rejected", "torrent");          // browser finishes it, then sends torrentPath
            case ProbeKind.Html when !expectsHtml && !fromMenu:
                return Reply("rejected", "html");             // browser resumes its own (working) request
            case ProbeKind.Html when !expectsHtml:
                Dispatcher.UIThread.Post(() => _ = Guard(Dialogs.MessageAsync(Owner, Strings.NewDownload, Strings.WarnHtml)));
                return Reply("accepted");
            case ProbeKind.Error when !fromMenu:
                return Reply("rejected", "unreachable");
        }

        Dispatcher.UIThread.Post(() => _ = Guard(OnBrowserDownloadAsync(add, ctx, probe)));
        return Reply("accepted");
    }

    private static async Task<byte[]?> ReadTorrentFileAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > HttpProbe.MaxTorrentBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path);
            return MonoTorrent.Torrent.TryLoad(bytes, out _) ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task OnBrowserDownloadAsync(DownloadAdd add, RequestContext ctx, ProbeResult probe)
    {
        var name = add.FileName ?? probe.FileName ?? FileNameResolver.Resolve(null, new Uri(add.FinalUrl ?? add.Url), add.Mime);

        // An expired download waiting for a fresh link? Never attach without asking.
        foreach (var candidate in Engine.Queue.FindRefreshCandidates(name, add.TotalBytes ?? probe.Size).OfType<HttpDownload>())
        {
            var attach = await Dialogs.ConfirmAsync(Owner, Strings.LinkMatchTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.LinkMatchBody, candidate.FileName), Strings.LinkMatchAttach, Strings.LinkMatchNew);
            if (attach && await Flows.RefreshLinkAsync(this, candidate, add.Url, ctx)) return;
            break;
        }

        await Flows.NewDownloadAsync(this, [add.Url], add.FileName, ctx, probe, night: add.Queue == "night");
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}

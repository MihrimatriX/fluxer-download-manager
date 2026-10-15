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
        Engine = new FluxerEngine(Program.DataDirectory);
        Loc.Apply(Engine.Settings.Language);
        ApplyTheme();
        _ = SetCurrentProcessExplicitAppUserModelID(ShellIntegration.Aumid);
        RegisterIntegration();
        MediaDownload.CleanupTemp(Engine.Tools);

        Main = new MainViewModel(this);
        Engine.Queue.Added += d => Dispatcher.UIThread.Post(() => Main.OnAdded(d));
        Engine.Queue.Removed += d => Dispatcher.UIThread.Post(() => Main.OnRemoved(d));
        Engine.Queue.StateChanged += OnStateChanged;
        Engine.Queue.Drained += () => Dispatcher.UIThread.Post(OnDrained);
        Engine.Queue.Load();
        Main.Reload();

        _pipe = new PipeServer(HandleMessageAsync);
        _pipe.Start();
        BuildTray();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();

        _ = CheckToolUpdateAsync();
        _ = CheckAppUpdateAsync();

        if (!args.Contains("--background")) ShowMainWindow();
        _ = HandleArgsAsync([.. args.Where(a => a != "--background")]);
    }

    // ================= window / tray / lifetime =================

    public void ShowMainWindow()
    {
        if (Window is null)
        {
            Window = new MainWindow { DataContext = Main };
            Window.Closing += (_, e) =>
            {
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

    private void BuildTray()
    {
        _tray?.Dispose();
        var menu = new NativeMenu();
        menu.Add(Item(Strings.TrayOpen, ShowMainWindow));
        menu.Add(Item(Strings.AddUrl, () => _ = Main.AddUrlCommand.ExecuteAsync(null)));
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
        _tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(app, [_tray]);

        static NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer?.Stop();
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
        Main.UpdateTotals(active.Count, items.Count(d => d.State == DownloadState.Queued), speed);
        RunScheduler();
    }

    private void RunScheduler()
    {
        var s = Engine.Settings;
        var now = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var key = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (key == _lastScheduleFire) return;
        if (s.ScheduleStart == now) { _lastScheduleFire = key; Engine.Queue.ResumeAll(); Log.Information("Scheduler: start"); }
        else if (s.ScheduleStop == now) { _lastScheduleFire = key; _ = Engine.Queue.PauseAllAsync(); Log.Information("Scheduler: stop"); }
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

    private void OnStateChanged(IDownload d)
    {
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

    private void RegisterIntegration()
    {
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

    private Task<Envelope> HandleMessageAsync(Envelope msg)
    {
        var accepted = Envelope.Create(msg.Id, MessageTypes.DownloadAddResult, new DownloadAddResult("accepted", null));
        switch (msg.Type)
        {
            case MessageTypes.Ping:
                return Task.FromResult(Envelope.Create(msg.Id, MessageTypes.Pong, new Pong(typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0")));
            case MessageTypes.StatusGet:
                return Task.FromResult(Envelope.Create(msg.Id, MessageTypes.Status, BuildStatus()));
            case MessageTypes.DownloadAdd when msg.PayloadAs<DownloadAdd>() is { } add:
                Dispatcher.UIThread.Post(() => _ = Guard(OnBrowserDownloadAsync(add)));
                return Task.FromResult(accepted);
            case MessageTypes.PageSend when msg.PayloadAs<PageSend>() is { } page:
                Dispatcher.UIThread.Post(() => _ = Guard(Flows.MediaAsync(this, page.PageUrl, page.Cookies)));
                return Task.FromResult(accepted);
            case MessageTypes.PageResources when msg.PayloadAs<PageResources>() is { } res:
                Dispatcher.UIThread.Post(() => _ = Guard(Flows.GrabAsync(this, res)));
                return Task.FromResult(accepted);
            case MessageTypes.InstanceArgs when msg.PayloadAs<InstanceArgs>() is { } ia:
                Dispatcher.UIThread.Post(() => _ = Guard(HandleArgsAsync(ia.Args, showWindow: true)));
                return Task.FromResult(accepted);
            default:
                return Task.FromResult(Envelope.Error(msg.Id, ErrorCodes.InvalidMessage, "unknown type"));
        }
    }

    private StatusReply BuildStatus()
    {
        var items = Engine.Queue.Items;
        var active = items.Where(d => d.State is DownloadState.Starting or DownloadState.Downloading).Select(d => (d, p: d.GetProgress())).ToList();
        return new StatusReply(active.Count, items.Count(d => d.State == DownloadState.Queued), active.Sum(x => x.p.BytesPerSecond),
            [.. active.OrderByDescending(x => x.p.BytesPerSecond).Take(5).Select(x => new StatusItem(x.d.FileName, x.p.Fraction, x.p.BytesPerSecond))]);
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

    private async Task OnBrowserDownloadAsync(DownloadAdd add)
    {
        if (add.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            await Flows.TorrentAsync(this, null, add.Url);
            return;
        }

        var ctx = new RequestContext(add.Referrer, add.UserAgent, CookieHeader(add.Cookies));
        var name = add.FileName ?? FileNameResolver.Resolve(null, new Uri(add.FinalUrl ?? add.Url), add.Mime);

        // An expired download waiting for a fresh link? Never attach without asking.
        foreach (var candidate in Engine.Queue.FindRefreshCandidates(name, add.TotalBytes).OfType<HttpDownload>())
        {
            var attach = await Dialogs.ConfirmAsync(Owner, Strings.LinkMatchTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.LinkMatchBody, candidate.FileName), Strings.LinkMatchAttach, Strings.LinkMatchNew);
            if (attach && await Flows.RefreshLinkAsync(this, candidate, add.Url, ctx)) return;
            break;
        }

        await Flows.NewDownloadAsync(this, [add.Url], add.FileName, ctx, add.TotalBytes);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}

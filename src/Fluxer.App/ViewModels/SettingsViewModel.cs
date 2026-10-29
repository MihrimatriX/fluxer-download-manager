using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Services;
using Fluxer.App.Views;
using Fluxer.Core;
using Fluxer.Core.Downloads;
using Fluxer.Core.Platform.Windows;
using Fluxer.Core.Torrent;
using Fluxer.Ipc;

namespace Fluxer.App.ViewModels;

public sealed record ThemeChoice(string Label, string IconKey)
{
    public Avalonia.Media.Geometry? Icon => Theme.Geometry(IconKey);
    public override string ToString() => Label;
}

public sealed record BrowserRow(string Browser, bool Ok)
{
    public Avalonia.Media.Geometry? Icon => Theme.Geometry(Ok ? "IcCheck" : "IcClose");
    public Avalonia.Media.IBrush Brush => Theme.Brush(Ok ? "OkBrush" : "ErrBrush");
    public string Text => Ok ? Strings.IntegrationOk : Strings.IntegrationMissing;
}

public sealed record SettingsSection(string Key, string Label)
{
    public Avalonia.Media.Geometry? Icon => Theme.Geometry(Key switch
    {
        "downloads" => "IcLogo", "speed" => "IcGauge", "categories" => "IcTag", "sites" => "IcLock", "torrent" => "IcTorrent", "browser" => "IcGlobe", "automation" => "IcBolt",
        "advanced" => "IcSliders", _ => "IcGear",
    });

    public override string ToString() => Label;
}

/// One speed profile card. Speeds in MB/s, 0 = unlimited.
public sealed partial class ProfileCard(int index, Action changed) : ObservableObject
{
    public int Index { get; } = index;
    public string Name => MainViewModel.ProfileName(Index);
    public Avalonia.Media.IBrush Swatch => Index switch
    {
        0 => Theme.Brush("SystemControlHighlightAccentBrush"),
        1 => Theme.Brush("InfoBrush"),
        _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#5B6478")),
    };
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private decimal? _downMb;
    [ObservableProperty] private decimal? _upMb;
    [ObservableProperty] private decimal? _slots;
    public bool Loading { get; set; }

    partial void OnDownMbChanged(decimal? value) { if (!Loading) changed(); }
    partial void OnUpMbChanged(decimal? value) { if (!Loading) changed(); }
    partial void OnSlotsChanged(decimal? value) { if (!Loading) changed(); }
}

/// One RSS feed in Settings → Torrent. The URL is fixed once added; remove and add again to change it.
public sealed partial class RssFeedRow(string url, Action changed) : ObservableObject
{
    public string Url { get; } = url;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _include = "";
    [ObservableProperty] private string _exclude = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FolderTip))] private string _saveDirectory = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasProblem;
    public string FolderTip => SaveDirectory.Length > 0 ? SaveDirectory : Strings.RssSaveFolder;
    public bool Loading { get; set; }
    private RssFeedState? _state;

    partial void OnEnabledChanged(bool value) => Changed();
    partial void OnIncludeChanged(string value) => Changed();
    partial void OnExcludeChanged(string value) => Changed();
    partial void OnSaveDirectoryChanged(string value) => Changed();

    private void Changed()
    {
        if (Loading) return;
        UpdateStatus(_state);
        changed();
    }

    public void UpdateStatus(RssFeedState? state)
    {
        _state = state;
        HasProblem = !RssFilter.IsValid(Include) || !RssFilter.IsValid(Exclude) || state?.LastError is not null;
        Status = !RssFilter.IsValid(Include) || !RssFilter.IsValid(Exclude) ? Strings.RssInvalidFilter
            : state?.LastCheck is not { } t ? Strings.RssNotChecked
            : state.LastError is { } err ? $"{Loc.When(t.ToLocalTime())} · {Loc.Error(err)}"
            : string.Format(CultureInfo.CurrentCulture, Strings.RssLastCheck, Loc.When(t.ToLocalTime()), state.LastAdded);
    }

    public RssFeed ToFeed() => new()
    {
        Url = Url,
        Enabled = Enabled,
        Include = string.IsNullOrWhiteSpace(Include) ? null : Include.Trim(),
        Exclude = string.IsNullOrWhiteSpace(Exclude) ? null : Exclude.Trim(),
        SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory) ? null : SaveDirectory.Trim(),
    };
}

/// Notification and sound for one event (finished, failed, queue done) in Settings → General.
public sealed partial class EventRow(string key, string label, Action changed) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty] private bool _notify;
    [ObservableProperty] private bool _sound;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SoundName))] private string? _soundFile;
    public string SoundName => SoundFile is { Length: > 0 } f ? Path.GetFileNameWithoutExtension(f) : Strings.SoundWindows;
    public bool Loading { get; set; }

    partial void OnNotifyChanged(bool value) { if (!Loading) changed(); }
    partial void OnSoundChanged(bool value) { if (!Loading) changed(); }
    partial void OnSoundFileChanged(string? value) { if (!Loading) changed(); }

    public void Load(EventFeedback f)
    {
        Loading = true;
        Notify = f.Notify;
        Sound = f.Sound;
        SoundFile = f.SoundFile;
        Loading = false;
    }

    public EventFeedback ToFeedback() => new() { Notify = Notify, Sound = Sound, SoundFile = SoundFile is { Length: > 0 } ? SoundFile : null };
}

/// Settings page. Every change is saved automatically (debounced); there is no Save button.
public sealed partial class SettingsViewModel : ObservableObject
{
    private const long KB = 1024, MB = 1024 * 1024;
    private readonly AppHost _host;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;
    private int _savedStamp;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, async (_, _) =>
        {
            _saveTimer!.Stop();
            await SaveAsync();
        });
        Profiles = [.. Enumerable.Range(0, 3).Select(i => new ProfileCard(i, QueueSave))];
        Events =
        [
            new("completed", Strings.EventCompleted, QueueSave), new("failed", Strings.EventFailed, QueueSave),
            new("queue", Strings.EventQueueDone, QueueSave),
        ];
        Sections =
        [
            new("general", Strings.SecGeneral), new("downloads", Strings.SecDownloads), new("speed", Strings.SecSpeedSchedule),
            new("categories", Strings.SecCategories), new("sites", Strings.SecSites), new("torrent", Strings.SecTorrent), new("browser", Strings.SecBrowserIntegration),
            new("automation", Strings.SecAutomation),
            new("advanced", Strings.TabAdvanced),
        ];
        _selectedSection = Sections[0];
        host.Engine.Rss.Checked += () => Dispatcher.UIThread.Post(RefreshRssStatus);
    }

    public string[] Languages => [Strings.LangSystem, "Türkçe", "English"];
    public ThemeChoice[] Themes => [new(Strings.ThemeLight, "IcSun"), new(Strings.ThemeDark, "IcMoon")];
    public string[] Conflicts => [Strings.ConflictRename, Strings.ConflictOverwrite];
    public string[] AfterQueueOptions => [Strings.AfterNothing, Strings.AfterSleep, Strings.AfterShutdown];
    public string[] Encryptions => [Strings.EncPrefer, Strings.EncRequire, Strings.EncOff];
    public ObservableCollection<BrowserRow> Browsers { get; } = [];
    public string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");
    public string FirefoxExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension-firefox");
    public string ExtensionId => Protocol.AllowedExtensionIds[0];
    public string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    // Navigation
    public IReadOnlyList<SettingsSection> Sections { get; }
    [ObservableProperty] private SettingsSection _selectedSection;
    /// Key of the visible section; MainViewModel sets it to open a page directly ("speed").
    public string Section
    {
        get => SelectedSection.Key;
        set => SelectedSection = Sections.FirstOrDefault(s => s.Key == value) ?? Sections[0];
    }
    partial void OnSelectedSectionChanged(SettingsSection value) => OnPropertyChanged(nameof(Section));

    // Appearance
    [ObservableProperty] private bool _compact;
    [ObservableProperty] private double _scalePercent = 100;
    [ObservableProperty] private bool _animations;
    [ObservableProperty] private bool _toolbarLabelsOn;
    public string ScaleText => Loc.Percent(ScalePercent / 100);
    partial void OnScalePercentChanged(double value) => OnPropertyChanged(nameof(ScaleText));

    // General
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private bool _showAddDialog;
    [ObservableProperty] private bool _showProgressWindow;
    [ObservableProperty] private bool _showCompletedCard;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _confirmExit;
    public IReadOnlyList<EventRow> Events { get; }
    // Automation
    [ObservableProperty] private bool _programOn;
    [ObservableProperty] private string _programLine = "";
    [ObservableProperty] private bool _programOnFailed;
    [ObservableProperty] private bool _webhookOn;
    [ObservableProperty] private string _webhookUrl = "";
    [ObservableProperty] private bool _webhookOnAdded;
    [ObservableProperty] private bool _webhookOnCompleted;
    [ObservableProperty] private bool _webhookOnFailed;
    [ObservableProperty] private string _webhookStatus = "";
    public string PlaceholderHelp => string.Join("   ", Fluxer.Core.Automation.DownloadHooks.Placeholders
        .Zip(Strings.PlaceholderNames.Split('|'), (p, name) => $"{p.Token} {name}"));
    // Downloads
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty] private bool _useCategoryFolders;
    [ObservableProperty] private int _conflictIndex;
    [ObservableProperty] private decimal? _connections;
    // Speed and schedule
    public IReadOnlyList<ProfileCard> Profiles { get; }
    [ObservableProperty] private int _brushIndex = 2;
    public string BrushName => MainViewModel.ProfileName(BrushIndex);
    partial void OnBrushIndexChanged(int value) => OnPropertyChanged(nameof(BrushName));
    [ObservableProperty] private bool _weekScheduleOn;
    [ObservableProperty] private string _weekSchedule = "";
    [ObservableProperty] private bool _scheduleStartOn;
    [ObservableProperty] private TimeSpan? _scheduleStart;
    [ObservableProperty] private bool _scheduleStopOn;
    [ObservableProperty] private TimeSpan? _scheduleStop;
    [ObservableProperty] private int _afterQueueIndex;
    // Torrent
    [ObservableProperty] private decimal? _listenPort;
    [ObservableProperty] private bool _dht;
    [ObservableProperty] private bool _peerExchange;
    [ObservableProperty] private bool _localPeerDiscovery;
    [ObservableProperty] private bool _portForwarding;
    [ObservableProperty] private int _encryptionIndex;
    [ObservableProperty] private decimal? _seedRatio;
    [ObservableProperty] private decimal? _seedMinutes;
    [ObservableProperty] private decimal? _torrentSlots;
    [ObservableProperty] private decimal? _maxPeers;
    [ObservableProperty] private bool _fileAssociations;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FirewallText))] private bool _firewallOpen = true;
    public string FirewallText => FirewallOpen ? Strings.FirewallOn : Strings.FirewallOff;
    public ObservableCollection<RssFeedRow> RssFeeds { get; } = [];
    [ObservableProperty] private decimal? _rssInterval;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddRssFeedCommand))] private string _newRssUrl = "";
    public bool HasRssFeeds => RssFeeds.Count > 0;
    // Advanced
    [ObservableProperty] private string _proxy = "";
    [ObservableProperty] private string _updateFeed = "";
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private bool _betaUpdates;
    [ObservableProperty] private bool _scanDownloads;
    [ObservableProperty] private bool _watchClipboard;
    [ObservableProperty] private bool _watchFolderOn;
    [ObservableProperty] private string _watchFolder = "";
    // Read-only info
    [ObservableProperty] private string _toolsText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUpdateTools))] private bool _toolsBusy;
    public bool CanUpdateTools => !ToolsBusy && _host.Engine.Tools.IsInstalled;
    [ObservableProperty] private bool _hasPendingUpdate;
    [ObservableProperty] private string _savedText = "";
    [ObservableProperty] private string _extensionStatus = "";

    private static readonly HashSet<string> NotSettings =
    [
        nameof(ScaleText), nameof(SelectedSection), nameof(Section), nameof(BrushIndex), nameof(BrushName),
        nameof(ToolsText), nameof(ToolsBusy), nameof(CanUpdateTools), nameof(HasPendingUpdate), nameof(SavedText),
        nameof(NewRssUrl), nameof(HasRssFeeds), nameof(FirewallOpen), nameof(FirewallText), nameof(ExtensionStatus),
        nameof(WebhookStatus), nameof(BackupText), nameof(DataText), nameof(QuotaUsedText),
    ];

    public void Load()
    {
        _loading = true;
        var s = _host.Engine.Settings;
        LanguageIndex = s.Language switch { "tr" => 1, "en" => 2, _ => 0 };
        Compact = s.Ui.Compact;
        ScalePercent = Math.Round(s.Ui.Scale * 100);
        Animations = s.Ui.Animations;
        ToolbarLabelsOn = s.Ui.ToolbarLabels;
        ThemeIndex = s.Theme == "Light" ? 0 : 1;
        Notifications = s.Notifications;
        ShowAddDialog = s.ShowAddDialog;
        ShowProgressWindow = s.ShowProgressWindow;
        ShowCompletedCard = s.ShowCompletedCard;
        StartWithWindows = OperatingSystem.IsWindows() && Autostart.IsEnabled();
        ConfirmExit = s.ConfirmExit;
        Events[0].Load(s.OnCompleted);
        Events[1].Load(s.OnFailed);
        Events[2].Load(s.OnQueueDone);
        ProgramOn = s.Automation.Program is { Length: > 0 };
        ProgramLine = s.Automation.Program ?? _lastProgram;
        ProgramOnFailed = s.Automation.ProgramOnFailed;
        WebhookOn = s.Automation.WebhookUrl is { Length: > 0 };
        WebhookUrl = s.Automation.WebhookUrl ?? _lastWebhook;
        WebhookOnAdded = s.Automation.WebhookOnAdded;
        WebhookOnCompleted = s.Automation.WebhookOnCompleted;
        WebhookOnFailed = s.Automation.WebhookOnFailed;
        WebhookStatus = "";
        DownloadDirectory = s.DownloadDirectory;
        UseCategoryFolders = s.UseCategoryFolders;
        ConflictIndex = s.Conflict == ConflictPolicy.Overwrite ? 1 : 0;
        Connections = s.ConnectionsPerDownload;
        LoadProfiles(s);
        WeekScheduleOn = s.WeekScheduleOn;
        WeekSchedule = s.WeekSchedule.Length == 168 ? s.WeekSchedule : AppSettings.DefaultWeek();
        ScheduleStartOn = s.ScheduleStart is not null;
        ScheduleStart = ParseTime(s.ScheduleStart) ?? new TimeSpan(1, 0, 0);
        ScheduleStopOn = s.ScheduleStop is not null;
        ScheduleStop = ParseTime(s.ScheduleStop) ?? new TimeSpan(8, 0, 0);
        AfterQueueIndex = s.AfterQueue switch { "Sleep" => 1, "Shutdown" => 2, _ => 0 };
        ListenPort = s.ListenPort;
        Dht = s.Dht;
        PeerExchange = s.PeerExchange;
        LocalPeerDiscovery = s.LocalPeerDiscovery;
        PortForwarding = s.PortForwarding;
        EncryptionIndex = s.Encryption switch { "require" => 1, "off" => 2, _ => 0 };
        SeedRatio = (decimal)s.SeedRatio;
        SeedMinutes = s.SeedMinutes;
        TorrentSlots = s.TorrentSlots;
        MaxPeers = s.MaxPeersPerTorrent;
        FileAssociations = s.RegisterFileAssociations;
        RssInterval = s.RssIntervalMinutes;
        RssFeeds.Clear();
        foreach (var f in s.RssFeeds)
        {
            var row = new RssFeedRow(f.Url, QueueSave) { Loading = true };
            row.Enabled = f.Enabled;
            row.Include = f.Include ?? "";
            row.Exclude = f.Exclude ?? "";
            row.SaveDirectory = f.SaveDirectory ?? "";
            row.Loading = false;
            RssFeeds.Add(row);
        }
        OnPropertyChanged(nameof(HasRssFeeds));
        RefreshRssStatus();
        Proxy = s.Proxy ?? "";
        UpdateFeed = s.UpdateFeed ?? "";
        AutoUpdate = s.AutoUpdate;
        BetaUpdates = s.BetaUpdates;
        ScanDownloads = s.ScanDownloads;
        WatchClipboard = s.WatchClipboard;
        WatchFolderOn = s.WatchFolder is not null;
        WatchFolder = s.WatchFolder ?? "";
        HasPendingUpdate = _host.HasPendingUpdate;
        LoadData(s);
        LoadCategories(s);
        LoadQueues(s);
        LoadSites(s);
        SavedText = "";
        _loading = false;
    }

    private void LoadProfiles(AppSettings s)
    {
        foreach (var card in Profiles)
        {
            var p = s.Profiles.ElementAtOrDefault(card.Index) ?? new SpeedProfile();
            card.Loading = true;
            card.DownMb = Math.Round((decimal)p.Down / MB, 2);
            card.UpMb = Math.Round((decimal)p.Up / MB, 2);
            card.Slots = p.Slots;
            card.IsActive = card.Index == s.ActiveProfile;
            card.Loading = false;
        }
    }

    /// The host switched profiles (status bar, schedule): only the highlight moves.
    public void ProfileApplied()
    {
        foreach (var card in Profiles) card.IsActive = card.Index == _host.Engine.Settings.ActiveProfile;
    }

    [RelayCommand]
    private Task PickProfile(ProfileCard card) => _host.UseProfileAsync(card.Index);

    [RelayCommand]
    private void PickBrush(string index) => BrushIndex = int.Parse(index, CultureInfo.InvariantCulture);

    /// Slower, read-only info (registry, tool versions): only when the page is opened.
    public void LoadInfo()
    {
        RefreshBrowsers();
        _ = RefreshToolsAsync();
        _ = RefreshFirewallAsync();
        ExtensionStatus = _host.ExtensionSeen is { } seen
            ? string.Format(CultureInfo.CurrentCulture, Strings.ExtensionSeenAt, seen.ToString("t", CultureInfo.CurrentCulture))
            : Strings.ExtensionNotSeen;
    }

    private async Task RefreshFirewallAsync() => FirewallOpen = await Task.Run(Flows.FirewallOpen);

    [RelayCommand]
    private async Task AllowFirewall()
    {
        await Task.Run(Flows.AllowFirewall);
        await RefreshFirewallAsync();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || NotSettings.Contains(e.PropertyName)) return;
        QueueSave();
    }

    private void QueueSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private async Task SaveAsync()
    {
        var engine = _host.Engine;
        var old = engine.Settings;
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old))!;
        s.Language = LanguageIndex switch { 1 => "tr", 2 => "en", _ => "" };
        s.Ui.Compact = Compact;
        s.Ui.Scale = Math.Clamp(ScalePercent / 100, 0.85, 1.3);
        s.Ui.Animations = Animations;
        s.Ui.ToolbarLabels = ToolbarLabelsOn;
        s.Theme = ThemeIndex == 0 ? "Light" : "Dark";
        s.Notifications = Notifications;
        s.ShowAddDialog = ShowAddDialog;
        s.ShowProgressWindow = ShowProgressWindow;
        s.ShowCompletedCard = ShowCompletedCard;
        s.ConfirmExit = ConfirmExit;
        s.OnCompleted = Events[0].ToFeedback();
        s.OnFailed = Events[1].ToFeedback();
        s.OnQueueDone = Events[2].ToFeedback();
        // Switched off, the text stays in the box for this session so turning it back on keeps it.
        _lastProgram = ProgramLine;
        _lastWebhook = WebhookUrl;
        s.Automation = new AutomationSettings
        {
            Program = ProgramOn && !string.IsNullOrWhiteSpace(ProgramLine) ? ProgramLine.Trim() : null,
            ProgramOnFailed = ProgramOnFailed,
            WebhookUrl = WebhookOn && !string.IsNullOrWhiteSpace(WebhookUrl) ? WebhookUrl.Trim() : null,
            WebhookOnAdded = WebhookOnAdded,
            WebhookOnCompleted = WebhookOnCompleted,
            WebhookOnFailed = WebhookOnFailed,
        };
        if (OperatingSystem.IsWindows() && Protocol.Instance.Length == 0 && StartWithWindows != Autostart.IsEnabled())
        {
            try { Autostart.Set(StartWithWindows, Program.ExePath); }
            catch (Exception e) { Serilog.Log.Warning(e, "Start with Windows could not be changed"); }
        }
        if (!string.IsNullOrWhiteSpace(DownloadDirectory)) s.DownloadDirectory = DownloadDirectory.Trim();
        s.UseCategoryFolders = UseCategoryFolders;
        s.Conflict = ConflictIndex == 1 ? ConflictPolicy.Overwrite : ConflictPolicy.Rename;
        s.ConnectionsPerDownload = (int)Math.Clamp(Connections ?? 8, 1, 32);
        s.Profiles = [.. Profiles.Select(c => new SpeedProfile
        {
            Down = (long)((c.DownMb ?? 0) * MB),
            Up = (long)((c.UpMb ?? 0) * MB),
            Slots = (int)Math.Clamp(c.Slots ?? 3, 1, 20),
        })];
        s.UseProfile(s.ActiveProfile); // edited values of the active profile apply at once
        s.WeekScheduleOn = WeekScheduleOn;
        s.WeekSchedule = WeekSchedule;
        s.ScheduleStart = ScheduleStartOn && ScheduleStart is { } a ? $"{a.Hours:00}:{a.Minutes:00}" : null;
        s.ScheduleStop = ScheduleStopOn && ScheduleStop is { } b ? $"{b.Hours:00}:{b.Minutes:00}" : null;
        s.AfterQueue = AfterQueueIndex switch { 1 => "Sleep", 2 => "Shutdown", _ => "None" };
        s.ListenPort = (int)Math.Clamp(ListenPort ?? 51413, 1024, 65535);
        s.Dht = Dht;
        s.PeerExchange = PeerExchange;
        s.LocalPeerDiscovery = LocalPeerDiscovery;
        s.PortForwarding = PortForwarding;
        s.Encryption = EncryptionIndex switch { 1 => "require", 2 => "off", _ => "prefer" };
        s.SeedRatio = (double)(SeedRatio ?? 1);
        s.SeedMinutes = (int)(SeedMinutes ?? 60);
        s.TorrentSlots = (int)Math.Clamp(TorrentSlots ?? 2, 1, 20);
        s.MaxPeersPerTorrent = (int)Math.Clamp(MaxPeers ?? 100, 5, 1000);
        s.RegisterFileAssociations = FileAssociations;
        s.RssIntervalMinutes = (int)Math.Clamp(RssInterval ?? 30, 5, 24 * 60);
        s.RssFeeds = [.. RssFeeds.Select(r => r.ToFeed())];
        s.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        s.UpdateFeed = string.IsNullOrWhiteSpace(UpdateFeed) ? null : UpdateFeed.Trim();
        s.AutoUpdate = AutoUpdate;
        s.BetaUpdates = BetaUpdates;
        s.ScanDownloads = ScanDownloads;
        s.WatchClipboard = WatchClipboard;
        s.WatchFolder = WatchFolderOn && !string.IsNullOrWhiteSpace(WatchFolder) ? WatchFolder.Trim() : null;
        s.BackupInterval = BackupSetting(BackupIndex);
        s.CategoryList = [.. CategoryRows.Select(r => r.ToCategory())];
        s.Queues = [.. QueueRows.Select(r => r.ToQueue())];
        SaveSites(s);
        var updatesChanged = s.AutoUpdate != old.AutoUpdate || s.BetaUpdates != old.BetaUpdates || s.UpdateFeed != old.UpdateFeed;
        var watchChanged = s.WatchFolder != old.WatchFolder || s.WatchClipboard != old.WatchClipboard;
        var rssChanged = s.RssIntervalMinutes != old.RssIntervalMinutes || JsonSerializer.Serialize(s.RssFeeds) != JsonSerializer.Serialize(old.RssFeeds);

        var uiChanged = s.Language != old.Language || s.Theme != old.Theme;
        var assocChanged = s.RegisterFileAssociations != old.RegisterFileAssociations;
        await engine.SaveSettingsAsync(s);
        if (assocChanged) _host.RegisterIntegration();
        if (watchChanged) _host.ApplyWatchers();
        if (rssChanged) engine.Rss.Reschedule();
        if (updatesChanged) _host.CheckForAppUpdate();
        if (Avalonia.Application.Current is { } app) Appearance.Apply(app, s.Ui);
        _host.Main.LoadUi();
        _host.Main.UpdateTotals();
        SavedText = Strings.SettingsSaved;
        // The confirmation fades after a moment instead of staying forever.
        var stamp = ++_savedStamp;
        Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (stamp == _savedStamp) SavedText = ""; }, TimeSpan.FromSeconds(2.5));
        if (uiChanged) _host.ReloadUi();
    }

    private string _lastProgram = "", _lastWebhook = "";

    private static TimeSpan? ParseTime(string? t) =>
        TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v.ToTimeSpan() : null;

    // ================= actions =================

    private void RefreshBrowsers()
    {
        Browsers.Clear();
        foreach (var (b, ok) in ShellIntegration.Status()) Browsers.Add(new BrowserRow(b, ok));
    }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (_host.Owner is { } w && await Dialogs.PickFolderAsync(w, DownloadDirectory) is { } path) DownloadDirectory = path;
    }

    [RelayCommand]
    private async Task BrowseWatchFolder()
    {
        if (_host.Owner is { } w && await Dialogs.PickFolderAsync(w, WatchFolder.Length > 0 ? WatchFolder : DownloadDirectory) is { } path)
        {
            WatchFolder = path;
            WatchFolderOn = true;
        }
    }

    private bool CanAddRssFeed() => Uri.TryCreate(NewRssUrl.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && RssFeeds.All(r => r.Url != NewRssUrl.Trim());

    [RelayCommand(CanExecute = nameof(CanAddRssFeed))]
    private void AddRssFeed()
    {
        var row = new RssFeedRow(NewRssUrl.Trim(), QueueSave);
        row.UpdateStatus(null);
        RssFeeds.Add(row);
        NewRssUrl = "";
        OnPropertyChanged(nameof(HasRssFeeds));
        QueueSave();
    }

    [RelayCommand]
    private void RemoveRssFeed(RssFeedRow row)
    {
        RssFeeds.Remove(row);
        OnPropertyChanged(nameof(HasRssFeeds));
        AddRssFeedCommand.NotifyCanExecuteChanged();
        QueueSave();
    }

    [RelayCommand]
    private async Task BrowseRssFolder(RssFeedRow row)
    {
        if (_host.Owner is { } w && await Dialogs.PickFolderAsync(w, row.SaveDirectory.Length > 0 ? row.SaveDirectory : DownloadDirectory) is { } path)
            row.SaveDirectory = path;
    }

    /// Saves first so a feed added a moment ago is part of the check.
    [RelayCommand]
    private async Task CheckRssNow()
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); await SaveAsync(); }
        _host.Engine.Rss.CheckNow();
    }

    private void RefreshRssStatus()
    {
        foreach (var row in RssFeeds) row.UpdateStatus(_host.Engine.Rss.StateOf(row.Url));
    }

    [RelayCommand]
    private async Task PickSound(EventRow row)
    {
        if (_host.Owner is not { } w) return;
        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.PickSound,
            FileTypeFilter = [new FilePickerFileType("WAV") { Patterns = ["*.wav"] }],
            SuggestedStartLocation = await w.StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(row.SoundFile) ?? media),
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            row.SoundFile = path;
            row.Sound = true;
        }
    }

    [RelayCommand]
    private static void PlaySound(EventRow row) => Sounds.Play(row.SoundFile, row.Key);

    [RelayCommand]
    private static void ResetSound(EventRow row) => row.SoundFile = null;

    [RelayCommand]
    private async Task BrowseProgram()
    {
        if (_host.Owner is not { } w) return;
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.HookProgram,
            FileTypeFilter = [new FilePickerFileType(Strings.Programs) { Patterns = ["*.exe", "*.cmd", "*.bat", "*.ps1"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            ProgramLine = $"\"{path}\" \"%F\"";
            ProgramOn = true;
        }
    }

    [RelayCommand]
    private async Task TestWebhook()
    {
        WebhookStatus = Strings.WebhookSending;
        WebhookStatus = await _host.TestWebhookAsync(WebhookUrl);
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        if (Directory.Exists(DownloadDirectory)) Process.Start("explorer.exe", $"\"{DownloadDirectory}\"");
    }

    [RelayCommand]
    private void RepairIntegration()
    {
        _host.RegisterIntegration();
        RefreshBrowsers();
    }

    /// Everything "Load unpacked" needs at hand: the browser's extensions page, the folder selected in Explorer
    /// (to drag onto that page) and its path on the clipboard (for the folder picker).
    [RelayCommand]
    private async Task InstallExtension()
    {
        if (!Directory.Exists(ExtensionFolder)) return;
        await Dialogs.SetClipboardTextAsync(_host.Owner, ExtensionFolder);
        if (ShellIntegration.ExtensionsPage() is (var exe, var page)) Process.Start(exe, page)?.Dispose();
        Process.Start("explorer.exe", $"/select,\"{ExtensionFolder}\"")?.Dispose();
        _host.AwaitingExtension = true;
    }

    [RelayCommand]
    private void OpenExtensionFolder()
    {
        if (Directory.Exists(ExtensionFolder)) Process.Start("explorer.exe", $"\"{ExtensionFolder}\"");
    }

    [RelayCommand]
    private void OpenFirefoxExtensionFolder()
    {
        if (Directory.Exists(FirefoxExtensionFolder)) Process.Start("explorer.exe", $"\"{FirefoxExtensionFolder}\"");
    }

    [RelayCommand]
    private Task CopyExtensionId() => Dialogs.SetClipboardTextAsync(_host.Owner, ExtensionId);

    [RelayCommand]
    private void OpenLogs()
    {
        var logs = Path.Combine(Program.DataDirectory, "logs");
        if (Directory.Exists(logs)) Process.Start("explorer.exe", $"\"{logs}\"");
    }

    [RelayCommand]
    private void RestartToUpdate() => _host.RestartToUpdate();

    [RelayCommand]
    private async Task ResetDefaults()
    {
        if (!await Dialogs.ConfirmAsync(_host.Owner, Strings.ResetDefaults, Strings.ResetDefaultsConfirm)) return;
        var old = _host.Engine.Settings;
        var fresh = new AppSettings
        {
            MediaDisclaimerAccepted = old.MediaDisclaimerAccepted,
            LastYtDlpCheck = old.LastYtDlpCheck,
            RecentFolders = old.RecentFolders,
            Ui = new UiSettings { DesignRevision = 2 },
        };
        await _host.Engine.SaveSettingsAsync(fresh);
        Load();
        _host.ReloadUi();
    }

    private async Task RefreshToolsAsync()
    {
        var tools = _host.Engine.Tools;
        OnPropertyChanged(nameof(CanUpdateTools));
        if (!tools.IsInstalled) { ToolsText = Strings.ToolsNotInstalled; return; }
        try { ToolsText = $"yt-dlp {await tools.GetYtDlpVersionAsync(CancellationToken.None)} · deno · ffmpeg"; }
        catch (Exception e) { ToolsText = e.Message; }
    }

    [RelayCommand]
    private async Task UpdateTools()
    {
        if (!_host.Engine.Tools.IsInstalled) return;
        ToolsBusy = true;
        try
        {
            var updated = await _host.Engine.Tools.UpdateAllAsync(CancellationToken.None);
            await RefreshToolsAsync();
            if (updated.Count == 0) ToolsText += " · " + Strings.UpToDate;
        }
        catch (Exception e) { ToolsText = Loc.Error(e.Message); }
        finally { ToolsBusy = false; }
    }

    [RelayCommand]
    private Task ExportDiagnostics() => ExportDiagnosticsAsync();

    /// Also offered by the "Fluxer closed unexpectedly" prompt on the next start.
    public async Task ExportDiagnosticsAsync()
    {
        if (_host.Owner is not { } w) return;
        var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"fluxer-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is { } path) WriteDiagnostics(_host.Engine.Settings, path);
    }

    /// Logs + settings (proxy credentials stripped) — never the database (cookies) or download URLs.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private static void WriteDiagnostics(AppSettings settings, string zipPath)
    {
        File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var logs = Path.Combine(Program.DataDirectory, "logs");
        if (Directory.Exists(logs))
            foreach (var f in Directory.EnumerateFiles(logs, "*.log"))
            {
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry("logs/" + Path.GetFileName(f)).Open();
                src.CopyTo(dst);
            }
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        if (copy.Proxy is { } p && Uri.TryCreate(p, UriKind.Absolute, out var u) && u.UserInfo.Length > 0)
            copy.Proxy = p.Replace(u.UserInfo + "@", "***@", StringComparison.Ordinal);
        copy.RecentFolders = [];
        // Feed URLs often carry a tracker passkey; keep only the host.
        foreach (var f in copy.RssFeeds) f.Url = Uri.TryCreate(f.Url, UriKind.Absolute, out var fu) ? fu.Host : "?";
        if (copy.Automation.WebhookUrl is { } hook) copy.Automation.WebhookUrl = Uri.TryCreate(hook, UriKind.Absolute, out var hu) ? hu.Host : "?";
        foreach (var site in copy.Sites)
        {
            if (site.UserName is not null) site.UserName = "***";
            site.PasswordProtected = null;
        }
        using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open()))
            w.Write(JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        using (var w = new StreamWriter(zip.CreateEntry("system.txt").Open()))
        {
            w.WriteLine($"Fluxer {typeof(App).Assembly.GetName().Version}");
            w.WriteLine($"OS {Environment.OSVersion}  .NET {Environment.Version}  x64={Environment.Is64BitProcess}");
            foreach (var (b, ok) in ShellIntegration.Status()) w.WriteLine($"{b}: {(ok ? "ok" : "missing")}");
        }
    }
}

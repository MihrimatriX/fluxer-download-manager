using Fluxer.Core.Torrent;

namespace Fluxer.Core.Downloads;

public enum ConflictPolicy { Rename, Overwrite, Ask }

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int HttpSlots { get; set; } = 3;
    public int TorrentSlots { get; set; } = 2;
    public int ConnectionsPerDownload { get; set; } = 8;
    public long HttpLimit { get; set; }            // B/s, 0 = unlimited
    public long TorrentDownloadLimit { get; set; }
    public long TorrentUploadLimit { get; set; }
    public double SeedRatio { get; set; } = 1.0;
    public int SeedMinutes { get; set; } = 60;
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;
    public string? Proxy { get; set; }             // "http://host:port", null = system
    public string Language { get; set; } = "";     // "" = system, "tr", "en"
    public string Theme { get; set; } = "Dark";    // Light | Dark
    public bool UseCategoryFolders { get; set; } = true;
    public string? ScheduleStart { get; set; } = "01:00"; // night queue start, "HH:mm" or null = manual only
    public string? ScheduleStop { get; set; }
    public SmartSpeed Smart { get; set; } = new();
    public ExtractSettings Extract { get; set; } = new();
    public RemoteSettings Remote { get; set; } = new();
    /// Plugins the user turned on, each with the exact content they approved.
    public List<Plugins.PluginGrant> Plugins { get; set; } = [];
    /// The user's own queues besides the main and night ones.
    public List<NamedQueue> Queues { get; set; } = [];

    /// Unlimited, balanced, background. The active one is copied into the limits above (UseProfile).
    public List<SpeedProfile> Profiles { get; set; } =
    [
        new() { Slots = 5 },
        new() { Down = 10 * MB, Up = 1 * MB, Slots = 3 },
        new() { Down = 2 * MB, Up = 200 * KB, Slots = 1 },
    ];
    public int ActiveProfile { get; set; }
    public bool WeekScheduleOn { get; set; }
    /// 7 days (Monday first) × 24 hours, one profile index digit per hour.
    public string WeekSchedule { get; set; } = DefaultWeek();

    // Torrent network
    public int ListenPort { get; set; } = 51413;
    public bool Dht { get; set; } = true;
    public bool PeerExchange { get; set; } = true;
    public bool LocalPeerDiscovery { get; set; } = true;
    public bool PortForwarding { get; set; } = true;
    public string Encryption { get; set; } = "prefer"; // prefer | require | off
    public int MaxPeersPerTorrent { get; set; } = 100;
    /// Extra trackers for public torrents: the user's own, plus a subscribed list refreshed daily (null = none).
    public List<string> ExtraTrackers { get; set; } = [];
    public string? TrackerListUrl { get; set; }
    public List<string> TrackerListCache { get; set; } = [];
    public long TrackerListAt { get; set; }
    /// Peer block list: a file, or an http(s) address downloaded weekly into the data folder (null = none).
    public string? IpFilterSource { get; set; }
    public long IpFilterAt { get; set; }
    /// Peers banned by hand from the Peers tab.
    public List<string> BannedPeers { get; set; } = [];
    /// Torrent search (Torznab: Jackett, Prowlarr…).
    public List<Torrent.TorznabIndexer> Indexers { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> AllExtraTrackers => Torrent.TrackerList.Merge(ExtraTrackers, TrackerListUrl is null ? [] : TrackerListCache);

    private const long KB = 1024, MB = 1024 * 1024;

    /// Nights unlimited, office hours in the background, the rest balanced.
    public static string DefaultWeek() => string.Concat(Enumerable.Range(0, 7 * 24).Select(i =>
    {
        int day = i / 24, hour = i % 24;
        return hour is >= 1 and < 7 ? '0' : day < 5 && hour is >= 9 and < 18 ? '2' : '1';
    }));

    /// Makes a speed profile current: its limits and slot count become the global ones.
    public void UseProfile(int index)
    {
        ActiveProfile = Math.Clamp(index, 0, Profiles.Count - 1);
        var p = Profiles[ActiveProfile];
        HttpLimit = TorrentDownloadLimit = p.Down;
        TorrentUploadLimit = p.Up;
        HttpSlots = Math.Max(1, p.Slots);
    }

    /// The profile the weekly schedule wants at `now`; null when the schedule is off or unreadable.
    public int? ScheduledProfile(DateTime now)
    {
        if (!WeekScheduleOn || WeekSchedule.Length != 7 * 24) return null;
        var monday0 = ((int)now.DayOfWeek + 6) % 7; // DayOfWeek starts on Sunday
        var digit = WeekSchedule[monday0 * 24 + now.Hour] - '0';
        return digit >= 0 && digit < Profiles.Count ? digit : null;
    }
    public string AfterQueue { get; set; } = "None"; // None | Sleep | Shutdown
    public bool ShowAddDialog { get; set; } = true;
    public UiSettings Ui { get; set; } = new();
    public bool ShowProgressWindow { get; set; } = true;
    public bool ShowCompletedCard { get; set; } = true;
    public List<string> RecentFolders { get; set; } = [];
    public bool Notifications { get; set; } = true;  // master switch for the per-event notifications below
    public EventFeedback OnCompleted { get; set; } = new();
    public EventFeedback OnFailed { get; set; } = new() { Sound = true };
    public EventFeedback OnQueueDone { get; set; } = new() { Notify = false };
    /// "Exit" from the tray asks first while downloads are running or waiting.
    public bool ConfirmExit { get; set; } = true;
    public AutomationSettings Automation { get; set; } = new();
    public string BackupInterval { get; set; } = "daily";   // off | daily | weekly
    public long LastBackupAt { get; set; }                   // Unix seconds
    public bool RegisterFileAssociations { get; set; } = true;
    public const string DefaultUpdateFeed = "https://github.com/MihrimatriX/fluxer-download-manager";
    public string? UpdateFeed { get; set; }        // Velopack feed URL or folder; null = DefaultUpdateFeed
    public bool AutoUpdate { get; set; } = true;
    public bool BetaUpdates { get; set; }          // GitHub pre-releases too
    public bool ScanDownloads { get; set; } = true; // hand finished files to the antivirus (IAttachmentExecute)
    public bool WatchClipboard { get; set; }       // offer copied download links
    public string? WatchFolder { get; set; }       // .torrent files dropped here are added and moved to "added"
    public List<RssFeed> RssFeeds { get; set; } = [];  // new torrents from these feeds are added automatically
    public int RssIntervalMinutes { get; set; } = 30;
    public bool MediaDisclaimerAccepted { get; set; }
    public bool FirewallAsked { get; set; }         // the first torrent offered the inbound firewall rule once
    public bool ToolsConsentGiven { get; set; }
    public long LastYtDlpCheck { get; set; }

    /// Per-site login, connections, headers and speed limit.
    public List<SiteRule> Sites { get; set; } = [];

    /// The most specific rule for a host (the longest pattern wins).
    public SiteRule? SiteRuleFor(string? host) =>
        host is { Length: > 0 } ? Sites.Where(r => r.Matches(host)).OrderByDescending(r => r.Host.Length).FirstOrDefault() : null;

    /// Built-in and user categories, in the order the filter row shows them.
    public List<Category> CategoryList { get; set; } = Category.Defaults();

    /// The category a file goes to: a site rule first (its extensions, if any, must match too), then extensions.
    public string? CategoryFor(string fileName, string? url = null)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;
        foreach (var c in CategoryList.Where(c => c.Sites.Count > 0))
            if (c.MatchesHost(host) && (c.Extensions.Count == 0 || c.Extensions.Contains(ext))) return c.Key;
        foreach (var c in CategoryList.Where(c => c.Sites.Count == 0))
            if (ext.Length > 0 && c.Extensions.Contains(ext)) return c.Key;
        return null;
    }

    /// The category's own folder, or "download folder\category".
    public string CategoryFolder(string category) =>
        CategoryList.FirstOrDefault(c => c.Key == category)?.Folder is { Length: > 0 } own ? own : Path.Combine(DownloadDirectory, category);

    /// Where a download of this category is saved when nobody picked a folder: the category's folder when category
    /// folders are on or the category has a folder of its own, else the download folder.
    public string FolderFor(string? category) =>
        category is { Length: > 0 } && (UseCategoryFolders || CategoryList.Any(c => c.Key == category && c.Folder is { Length: > 0 }))
            ? CategoryFolder(category) : DownloadDirectory;

    /// Settings saved by an older version get the current design's defaults once. Revision 1 (0.8): dark theme,
    /// compact rows, night queue at 01:00. Revision 2 (the compact redesign): light or dark only ("follow system"
    /// becomes dark), the toolbar shows icons only, and columns and the detail panel start fresh.
    /// Returns true when something changed and should be saved.
    public bool UpgradeDesign()
    {
        if (Ui.DesignRevision >= 2) return false;
        if (Ui.DesignRevision < 1)
        {
            Theme = "Dark";
            Ui.Compact = true;
            ScheduleStart ??= "01:00";
        }
        Ui.DesignRevision = 2;
        if (Theme != "Light") Theme = "Dark";
        Ui.ToolbarLabels = false;
        Ui.DetailHeight = new UiSettings().DetailHeight;
        Ui.ColumnOrder.Clear();
        Ui.ColumnWidths.Clear();
        Ui.HiddenColumns.Clear();
        return true;
    }
}

/// A Windows notification and/or a sound for one kind of event. SoundFile null = the Windows sound for it.
public sealed class EventFeedback
{
    public bool Notify { get; set; } = true;
    public bool Sound { get; set; }
    public string? SoundFile { get; set; }
}

/// Speed rules that follow the PC instead of the clock.
public sealed class SmartSpeed
{
    /// While someone works in another program, use BusyProfile; back to the usual profile after IdleMinutes.
    public bool BusyOn { get; set; }
    public int BusyProfile { get; set; } = 2;
    public int IdleMinutes { get; set; } = 3;
    /// On battery below this percentage everything pauses (0 = off); it continues on mains power.
    public int BatteryPercent { get; set; }
    /// No more than QuotaMb in any QuotaHours (0 = off); downloads pause until the window frees up.
    public long QuotaMb { get; set; }
    public int QuotaHours { get; set; } = 24;
}

/// Remote control: REST API, aria2 JSON-RPC and the web page (Settings → Automation → Remote access).
public sealed class RemoteSettings
{
    public bool On { get; set; }
    /// aria2's default, so its front-ends find Fluxer without changes.
    public int Port { get; set; } = 6800;
    /// Listen on every network interface instead of 127.0.0.1 only.
    public bool Lan { get; set; }
    public string? KeyProtected { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Key
    {
        get
        {
            if (KeyProtected is not { Length: > 0 } p) return null;
            try { return Platform.Windows.Dpapi.Unprotect(Convert.FromBase64String(p)); }
            catch (FormatException) { return null; }
        }
        set => KeyProtected = Platform.Windows.Dpapi.Protect(value) is { } blob ? Convert.ToBase64String(blob) : null;
    }
}

/// Unpacking finished archives (Settings → Downloads → Archives).
public sealed class ExtractSettings
{
    public bool Auto { get; set; }
    /// The archive (every part) goes once everything came out.
    public bool DeleteAfter { get; set; }
    /// Tried in turn on encrypted archives: one per line, DPAPI-encrypted like site passwords.
    public string? PasswordsProtected { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> Passwords
    {
        get
        {
            if (PasswordsProtected is not { Length: > 0 } p) return [];
            try { return [.. (Platform.Windows.Dpapi.Unprotect(Convert.FromBase64String(p)) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)]; }
            catch (FormatException) { return []; }
        }
        set => PasswordsProtected = Platform.Windows.Dpapi.Protect(string.Join('\n', value.Where(x => x.Length > 0))) is { } blob ? Convert.ToBase64String(blob) : null;
    }
}

/// What runs when a download finishes or fails.
public sealed class AutomationSettings
{
    /// Command line; %F path, %N name, %D folder, %K category, %Z size, %U address, %E event, %I id.
    public string? Program { get; set; }
    public bool ProgramOnFailed { get; set; }
    public string? WebhookUrl { get; set; }
    public bool WebhookOnAdded { get; set; }
    public bool WebhookOnCompleted { get; set; } = true;
    public bool WebhookOnFailed { get; set; } = true;
}

/// One speed profile; 0 = unlimited.
public sealed class SpeedProfile
{
    public long Down { get; set; }
    public long Up { get; set; }
    public int Slots { get; set; } = 3;
}

/// Look and layout the user can change; remembered between sessions.
public sealed class UiSettings
{
    public bool Compact { get; set; } = true;           // 32 px rows; off = 40 px
    public double Scale { get; set; } = 1.0;            // 0.85 – 1.3
    public bool Animations { get; set; } = true;
    public bool ToolbarLabels { get; set; }             // text next to the title bar's command icons
    public bool DetailVisible { get; set; }
    public double DetailHeight { get; set; } = 210;     // the detail panel sits under the list
    public int DesignRevision { get; set; }
    public List<string> HiddenColumns { get; set; } = [];
    public Dictionary<string, double> ColumnWidths { get; set; } = [];
    public List<string> ColumnOrder { get; set; } = [];
    public double[]? WindowBounds { get; set; }         // x, y, width, height
    /// The floating drop target and where it was left (screen pixels).
    public bool DropTarget { get; set; }
    public int[]? DropTargetPosition { get; set; }
    public bool WindowMaximized { get; set; }
}

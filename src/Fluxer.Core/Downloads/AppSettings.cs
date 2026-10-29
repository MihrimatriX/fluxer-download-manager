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
    public string Theme { get; set; } = "Dark";    // System | Light | Dark
    public bool UseCategoryFolders { get; set; } = true;
    public string? ScheduleStart { get; set; } = "01:00"; // night queue start, "HH:mm" or null = manual only
    public string? ScheduleStop { get; set; }

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
    public bool Notifications { get; set; } = true;
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

    public Dictionary<string, string[]> Categories { get; set; } = new()
    {
        ["Video"] = ["mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v"],
        ["Music"] = ["mp3", "flac", "wav", "aac", "ogg", "m4a", "opus", "wma"],
        ["Documents"] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "epub", "odt"],
        ["Archives"] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso"],
        ["Programs"] = ["exe", "msi", "msix", "appx", "bat", "apk"],
    };

    public string? CategoryFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        foreach (var (cat, exts) in Categories)
            if (exts.Contains(ext)) return cat;
        return null;
    }

    /// Settings saved by an older version get the current design's defaults once. Revision 1 (0.8): dark theme,
    /// compact rows, night queue at 01:00. Revision 2 (the compact redesign): the logo blue replaces the old orange,
    /// the neumorphic style is gone, the toolbar shows icons only, and columns and the detail panel start fresh.
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
        if (Ui.Accent == "#F5A623") Ui.Accent = null;
        if (Ui.Backdrop is not ("mica" or "glass")) Ui.Backdrop = "none";
        Ui.ToolbarLabels = false;
        Ui.DetailHeight = new UiSettings().DetailHeight;
        Ui.ColumnOrder.Clear();
        Ui.ColumnWidths.Clear();
        Ui.HiddenColumns.Clear();
        return true;
    }
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
    public string? Accent { get; set; }                 // "#RRGGBB", null = the logo blue
    public string Backdrop { get; set; } = "none";      // "none" (solid), "mica" (Windows 11), "glass" (acrylic blur)
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
    public bool WindowMaximized { get; set; }
}

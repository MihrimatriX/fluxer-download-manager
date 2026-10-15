using System.Globalization;
using Fluxer.Core.Downloads;

namespace Fluxer.App;

/// Culture switching and the few places where codes become user text.
public static class Loc
{
    public static void Apply(string language)
    {
        var culture = string.IsNullOrEmpty(language)
            ? (CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "tr" ? new CultureInfo("tr-TR") : new CultureInfo("en-US"))
            : new CultureInfo(language == "tr" ? "tr-TR" : "en-US");
        Strings.Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    public static string State(DownloadState s) => s switch
    {
        DownloadState.Queued => Strings.StateQueued,
        DownloadState.Starting => Strings.StateStarting,
        DownloadState.Downloading => Strings.StateDownloading,
        DownloadState.Paused => Strings.StatePaused,
        DownloadState.NeedsLinkRefresh => Strings.StateNeedsLinkRefresh,
        DownloadState.DiskFull => Strings.StateDiskFull,
        DownloadState.Verifying => Strings.StateVerifying,
        DownloadState.Seeding => Strings.StateSeeding,
        DownloadState.Completed => Strings.StateCompleted,
        DownloadState.Failed => Strings.StateFailed,
        _ => s.ToString(),
    };

    public static string Error(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        if (code.StartsWith("http-", StringComparison.Ordinal)) return string.Format(CultureInfo.CurrentCulture, Strings.ErrHttp, code[5..]);
        if (code.StartsWith("tool-hash-mismatch", StringComparison.Ordinal)) return Strings.ErrToolHash;
        return code switch
        {
            "disk-full" => Strings.ErrDiskFull,
            "got-html" => Strings.ErrGotHtml,
            "file-changed" => Strings.ErrFileChanged,
            "hash-mismatch" => Strings.ErrHashMismatch,
            "link-expired" => Strings.ErrLinkExpired,
            "network" => Strings.ErrNetwork,
            "media-signin" => Strings.ErrMediaSignin,
            "media-drm" => Strings.ErrMediaDrm,
            "media-unsupported" => Strings.ErrMediaUnsupported,
            "media-format" => Strings.ErrMediaFormat,
            "media-forbidden" => Strings.ErrMediaForbidden,
            "media-private" => Strings.ErrMediaPrivate,
            "media-failed" => Strings.ErrMediaFailed,
            "media-tools-missing" => Strings.ErrMediaToolsMissing,
            _ => code,
        };
    }

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b || b < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{b} B" : v.ToString(v >= 100 ? "F0" : "F1", CultureInfo.CurrentCulture) + " " + units[u];
    }

    public static string Speed(long bps) => bps > 0 ? Bytes(bps) + "/s" : "";

    public static string Eta(TimeSpan? t) => t switch
    {
        null => "",
        { TotalDays: >= 1 } d => $"{(int)d.TotalDays}d {d.Hours}h",
        { TotalHours: >= 1 } h => $"{(int)h.TotalHours}:{h.Minutes:00}:{h.Seconds:00}",
        { } m => $"{m.Minutes}:{m.Seconds:00}",
    };
}

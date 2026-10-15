using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.Core.Downloads;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

/// One line of a download's activity log (detail panel, "Log" tab). Level: 0 info, 1 warning, 2 error.
public sealed record LogLine(string Time, string Message, int Level)
{
    public IBrush Brush => Level switch { 2 => Theme.Brush("ErrTextBrush"), 1 => Theme.Brush("WarnTextBrush"), _ => Theme.Brush("Text2Brush") };
}

/// One row. Refresh() pulls a snapshot from the engine; only properties that changed raise events.
/// Text properties are for display; the raw ones (SizeBytes, SpeedValue, …) are the sort keys.
public sealed partial class DownloadItemViewModel(IDownload download, Func<IDownload, int?> queuePosition) : ObservableObject
{
    public IDownload Download { get; } = download;
    public long Id => Download.Id;
    public DateTime AddedAt { get; } = DateTimeOffset.FromUnixTimeMilliseconds(download.Record.CreatedAt).LocalDateTime;
    /// "Today 13:48", "Yesterday 21:12", otherwise a short date.
    public string AddedText => Loc.When(AddedAt);
    public bool IsTorrent => Download.Kind == DownloadKind.Torrent;

    /// What a screen reader announces for the row.
    public string AccessibleText => string.Join(", ", new[] { Name, StatusText, SizeText, SpeedText }.Where(s => s.Length > 0));

    public override string ToString() => Name;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Geometry? _icon;
    [ObservableProperty] private IBrush _iconBrush = Brushes.Gray;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private IBrush _speedBrush = Brushes.Gray;
    [ObservableProperty] private long _speedValue;
    [ObservableProperty] private string _upSpeedText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private IBrush _statusBrush = Brushes.Gray;
    [ObservableProperty] private int _stateOrder;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isSeeding;
    /// Paused, queued, night queue: the bar is grey.
    [ObservableProperty] private bool _isIdle;
    [ObservableProperty] private bool _canPause;
    /// "Torrent · 8,4 MB/s" (tray popup).
    [ObservableProperty] private string _metaText = "";

    /// Row check box; kept in step with the grid selection by MainWindow.
    [ObservableProperty] private bool _isChecked;

    public ObservableCollection<LogLine> Log { get; } = [];
    private DownloadState? _loggedState;

    public bool IsRunningState => Download.State is DownloadState.Starting or DownloadState.Downloading
        or DownloadState.Seeding or DownloadState.Verifying;

    public void Refresh()
    {
        var d = Download;
        var p = d.GetProgress();
        var state = d.State;
        Name = d.FileName.Length > 0 ? d.FileName : d.Record.Http?.Url ?? d.Record.Media?.PageUrl ?? "…";
        Icon = Theme.Geometry(IconKey(d));
        IconBrush = Theme.Brush(IsTorrent ? "InfoTextBrush" : "Text2Brush");
        IsActive = IsRunningState;
        Indeterminate = IsActive && p.TotalBytes is null or 0 && state != DownloadState.Seeding;
        var fraction = state is DownloadState.Completed or DownloadState.Seeding ? 1 : p.Fraction;
        Progress = fraction * 100;
        PercentText = Loc.Percent(fraction);
        SizeBytes = p.TotalBytes ?? p.BytesDone;
        SizeText = Loc.Bytes(p.TotalBytes ?? (p.BytesDone > 0 ? p.BytesDone : null));
        DoneText = $"{Loc.Bytes(state is DownloadState.Completed or DownloadState.Seeding ? p.TotalBytes ?? p.BytesDone : p.BytesDone)} / {Loc.Bytes(p.TotalBytes)}";

        var up = d is TorrentDownload t ? t.UploadRate : 0;
        UpSpeedText = Loc.Speed(up) is { Length: > 0 } u ? u : "0 B/s";
        SpeedValue = state == DownloadState.Downloading ? p.BytesPerSecond : 0;
        (SpeedText, SpeedBrush) = state switch
        {
            DownloadState.Downloading or DownloadState.Starting or DownloadState.Verifying when p.BytesPerSecond > 0 => (Loc.Speed(p.BytesPerSecond), Theme.Brush("TextBrush")),
            DownloadState.Seeding => ("↑ " + UpSpeedText, Theme.Brush("InfoTextBrush")),
            _ => ("—", Theme.Brush("FaintBrush")),
        };

        IsError = state is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh;
        IsDone = state == DownloadState.Completed;
        IsSeeding = state == DownloadState.Seeding;
        IsIdle = state is DownloadState.Queued or DownloadState.Paused or DownloadState.Scheduled;
        CanPause = IsActive || state is DownloadState.Queued or DownloadState.Scheduled;
        (StatusText, StatusBrush) = Status(d, p);
        MetaText = $"{(d.Kind switch { DownloadKind.Torrent => "Torrent", DownloadKind.Media => "Video", _ => "HTTP" })} · {SpeedText}";
        StateOrder = state switch
        {
            DownloadState.Downloading or DownloadState.Starting or DownloadState.Verifying => 0,
            DownloadState.Seeding => 1,
            DownloadState.Queued => 2,
            DownloadState.Scheduled => 3,
            DownloadState.Paused => 4,
            DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh => 5,
            _ => 6,
        };
        OnPropertyChanged(nameof(AccessibleText));
        LogTransition(state, d.Record.Error);
    }

    /// Records state changes while the app runs ("13:48 Downloading").
    private void LogTransition(DownloadState state, string? error)
    {
        if (_loggedState is null)
        {
            Log.Add(new LogLine(AddedAt.ToString("HH:mm", CultureInfo.CurrentCulture), Strings.LogAdded, 0));
            _loggedState = state;
            if (error is null) return;
        }
        else if (_loggedState == state) return;
        _loggedState = state;
        var level = error is not null ? 2 : state == DownloadState.NeedsLinkRefresh ? 1 : 0;
        var text = error is not null ? $"{Loc.State(state)} · {Loc.Error(error)}" : Loc.State(state);
        Log.Add(new LogLine(DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture), text, level));
        while (Log.Count > 50) Log.RemoveAt(0);
    }

    private (string, IBrush) Status(IDownload d, DownloadProgress p)
    {
        var state = d.State;
        var error = Loc.Error(d.Record.Error);
        if (state is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh)
            return (error.Length > 0 ? $"{Strings.ErrorShort} · {error}" : Loc.State(state), Theme.Brush("ErrBrush"));
        switch (state)
        {
            case DownloadState.Downloading when p.Eta is { } eta:
                return (string.Format(CultureInfo.CurrentCulture, Strings.EtaLeft, Loc.EtaWords(eta)), Theme.Brush("TextBrush"));
            case DownloadState.Downloading or DownloadState.Starting or DownloadState.Verifying:
                return (Loc.State(state), Theme.Brush("TextBrush"));
            case DownloadState.Seeding when d is TorrentDownload t && p.TotalBytes > 0:
                var ratio = (double)t.UploadedBytes / p.TotalBytes.Value;
                return (string.Format(CultureInfo.CurrentCulture, Strings.SeedRatioFormat, ratio.ToString("0.00", CultureInfo.CurrentCulture)), Theme.Brush("InfoTextBrush"));
            case DownloadState.Seeding:
                return (Loc.State(state), Theme.Brush("InfoTextBrush"));
            case DownloadState.Queued when queuePosition(d) is { } pos:
                return (string.Format(CultureInfo.CurrentCulture, Strings.QueuedAt, pos), Theme.Brush("SubtleBrush"));
            case DownloadState.Scheduled:
                return (NightQueueLabel(), Theme.Brush("SubtleBrush"));
            case DownloadState.Completed:
                return (Loc.State(state), Theme.Brush("OkBrush"));
            default:
                return (Loc.State(state), Theme.Brush("SubtleBrush"));
        }
    }

    public static string NightQueueLabel() =>
        Services.AppHost.Current?.Engine?.Settings.ScheduleStart is { } at
            ? string.Format(CultureInfo.CurrentCulture, Strings.NightQueueAt, at)
            : Strings.NightQueueManual;

    private static string IconKey(IDownload d)
    {
        if (d.Kind == DownloadKind.Torrent) return "IcTorrent";
        return (d.Record.Category ?? "") switch
        {
            "Video" => "IcVideo",
            "Music" => "IcMusic",
            "Documents" => "IcDoc",
            "Archives" => "IcArchive",
            "Programs" => "IcApp",
            _ => "IcFile",
        };
    }
}

/// Theme resources for code that builds brushes and icons (follow the light/dark variant at lookup time).
public static class Theme
{
    public static IBrush Brush(string key)
    {
        var app = Avalonia.Application.Current;
        return app?.TryFindResource(key, app.ActualThemeVariant, out var v) == true && v is IBrush b ? b : Brushes.Gray;
    }

    public static Geometry? Geometry(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var v) == true ? v as Geometry : null;
}

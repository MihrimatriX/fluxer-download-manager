using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// One row. Refresh() pulls a snapshot from the engine; only properties that changed raise events.
/// Text properties are for display; the raw ones (SizeBytes, SpeedValue, …) are the sort keys.
public sealed partial class DownloadItemViewModel(IDownload download, Func<IDownload, int?> queuePosition) : ObservableObject
{
    public IDownload Download { get; } = download;
    public long Id => Download.Id;
    public DateTime AddedAt { get; } = DateTimeOffset.FromUnixTimeMilliseconds(download.Record.CreatedAt).LocalDateTime;
    /// Today: time only; otherwise a short date.
    public string AddedText => AddedAt.Date == DateTime.Today
        ? AddedAt.ToString("t", CultureInfo.CurrentCulture)
        : AddedAt.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    /// What a screen reader announces for the row.
    public string AccessibleText => string.Join(", ", new[] { Name, StatusText, SizeText, SpeedText, EtaText }.Where(s => s.Length > 0));

    public override string ToString() => Name;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _glyph = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private long _speedValue;
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private double _etaSeconds = double.MaxValue;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _stateOrder;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isSeeding;
    /// Row action: true → the button pauses, false → it resumes.
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private string _toggleGlyph = "";

    /// Row check box; kept in step with the grid selection by MainWindow.
    [ObservableProperty] private bool _isChecked;
    /// File-type colour (icon tile) and state colour (status pill), each with a soft background.
    [ObservableProperty] private IBrush _tint = Gray.Solid;
    [ObservableProperty] private IBrush _tintSoft = Gray.Soft;
    [ObservableProperty] private IBrush _stateBrush = Gray.Solid;
    [ObservableProperty] private IBrush _stateSoft = Gray.Soft;

    public sealed record Pair(IBrush Solid, IBrush Soft)
    {
        public static Pair Of(Color c) => new(new SolidColorBrush(c), new SolidColorBrush(c, 0.16));
    }

    private static readonly Pair Gray = Pair.Of(Color.Parse("#8A8A8E"));
    private static readonly Dictionary<string, Pair> Tints = new()
    {
        ["Video"] = Pair.Of(Color.Parse("#8B5CF6")), ["Music"] = Pair.Of(Color.Parse("#EC4899")),
        ["Documents"] = Pair.Of(Color.Parse("#3B82F6")), ["Archives"] = Pair.Of(Color.Parse("#F59E0B")),
        ["Programs"] = Pair.Of(Color.Parse("#10B981")), ["Torrent"] = Pair.Of(Color.Parse("#06B6D4")),
    };
    private static readonly Pair Red = Pair.Of(Color.Parse("#E5484D")), Green = Pair.Of(Color.Parse("#30A46C")),
        Purple = Pair.Of(Color.Parse("#8E4EC6")), Amber = Pair.Of(Color.Parse("#F59E0B"));
    private static Pair? _accent;

    /// Running rows use the accent; rebuilt only when the accent changes.
    private static Pair Accent()
    {
        var c = Avalonia.Application.Current?.TryFindResource("SystemAccentColor", Avalonia.Application.Current.ActualThemeVariant, out var v) == true && v is Color col
            ? col : Color.Parse("#0A84FF");
        if (_accent?.Solid is not ISolidColorBrush b || b.Color != c) _accent = Pair.Of(c);
        return _accent;
    }

    public bool IsRunningState => Download.State is DownloadState.Starting or DownloadState.Downloading
        or DownloadState.Seeding or DownloadState.Verifying;

    public void Refresh()
    {
        var d = Download;
        var p = d.GetProgress();
        var state = d.State;
        Name = d.FileName.Length > 0 ? d.FileName : d.Record.Http?.Url ?? d.Record.Media?.PageUrl ?? "…";
        Glyph = GlyphFor(d);
        var tint = Tints.GetValueOrDefault(d.Kind == DownloadKind.Torrent ? "Torrent" : d.Record.Category ?? "", Gray);
        (Tint, TintSoft) = (tint.Solid, tint.Soft);
        IsActive = IsRunningState;
        Indeterminate = IsActive && p.TotalBytes is null or 0;
        Progress = state == DownloadState.Completed ? 100 : p.Fraction * 100;
        SizeBytes = p.TotalBytes ?? p.BytesDone;
        SizeText = state == DownloadState.Completed || p.TotalBytes is null
            ? Loc.Bytes(p.TotalBytes ?? p.BytesDone)
            : $"{Loc.Bytes(p.BytesDone)} / {Loc.Bytes(p.TotalBytes)}";
        SpeedValue = IsActive ? p.BytesPerSecond : 0;
        SpeedText = IsActive ? Loc.Speed(p.BytesPerSecond) : "";
        EtaSeconds = state == DownloadState.Downloading && p.Eta is { } eta ? eta.TotalSeconds : double.MaxValue;
        EtaText = state == DownloadState.Downloading ? Loc.Eta(p.Eta) : "";
        StatusText = Status(d, p);
        OnPropertyChanged(nameof(AccessibleText));
        StateOrder = state switch
        {
            DownloadState.Downloading or DownloadState.Starting or DownloadState.Verifying => 0,
            DownloadState.Seeding => 1,
            DownloadState.Queued => 2,
            DownloadState.Paused => 3,
            DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh => 4,
            _ => 5,
        };
        IsError = state is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh;
        IsDone = state == DownloadState.Completed;
        IsSeeding = state == DownloadState.Seeding;
        CanPause = IsActive || state == DownloadState.Queued;
        var sp = IsError ? Red : IsDone ? Green : IsSeeding ? Purple : IsActive ? Accent() : state == DownloadState.Queued ? Amber : Gray;
        (StateBrush, StateSoft) = (sp.Solid, sp.Soft);
        ToggleGlyph = CanPause ? "" : state == DownloadState.Completed ? "" : ""; // pause / folder / play
    }

    private string Status(IDownload d, DownloadProgress p)
    {
        var state = d.State;
        var error = Loc.Error(d.Record.Error);
        if (error.Length > 0) return $"{Loc.State(state)} · {error}";
        if (state == DownloadState.Queued && queuePosition(d) is { } pos)
            return string.Format(CultureInfo.CurrentCulture, Strings.QueuedAt, pos);
        if (state == DownloadState.Downloading && p.TotalBytes > 0)
            return Loc.Percent(p.Fraction, 1) + (p.Connections > 1 ? $" · {p.Connections} {Strings.ConnectionsShort}" : "");
        return Loc.State(state);
    }

    /// Segoe Fluent Icons code points.
    private static string GlyphFor(IDownload d)
    {
        if (d.Kind == DownloadKind.Torrent) return "";
        return (d.Record.Category ?? "") switch
        {
            "Video" => "",
            "Music" => "",
            "Documents" => "",
            "Archives" => "",
            "Programs" => "",
            _ => "",
        };
    }
}

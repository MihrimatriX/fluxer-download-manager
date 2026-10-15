using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Controls;
using Fluxer.App.Services;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

/// One live connection (HTTP segment) in the progress window.
public sealed partial class ConnectionRow(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Number => (Index + 1).ToString(CultureInfo.CurrentCulture);
    public IBrush Dot { get; } = new SolidColorBrush(SegmentMap.ColorFor(index));
    [ObservableProperty] private string _range = "";
    [ObservableProperty] private string _done = "";
    [ObservableProperty] private string _speed = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _isActive;
    internal long LastPosition = -1;
    internal long LastTicks;
}

/// The per-download window: live progress while running, a summary card once completed.
public sealed partial class DownloadWindowViewModel : ObservableObject
{
    private const int HistoryLength = 120; // 30 s at 4 Hz
    private readonly AppHost _host;
    private readonly Queue<double> _history = new();
    private readonly Stopwatch _elapsed = new();
    private long _bytesAtStart = -1;
    private DownloadState _lastState;
    private int _slow;

    public DownloadWindowViewModel(AppHost host, IDownload download, bool card)
    {
        _host = host;
        Download = download;
        IsCard = card;
        _lastState = download.State;
        var cat = download.Kind == DownloadKind.Torrent ? "Torrent" : download.Record.Category;
        Glyph = cat switch
        {
            "Video" => "", "Music" => "", "Documents" => "", "Archives" => "",
            "Programs" => "", "Torrent" => "", _ => "",
        };
        TileBrush = new SolidColorBrush(Color.Parse(cat switch
        {
            "Video" => "#8B5CF6", "Music" => "#EC4899", "Documents" => "#3B82F6", "Archives" => "#F59E0B",
            "Programs" => "#10B981", "Torrent" => "#06B6D4", _ => "#64748B",
        }));
        Host = download.Record.Http?.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host
            : download.Record.Media?.PageUrl is { } p && Uri.TryCreate(p, UriKind.Absolute, out var puri) ? puri.Host
            : download.Kind == DownloadKind.Torrent ? "BitTorrent" : "";
        IsHttp = download is HttpDownload;
        IsTorrent = download is TorrentDownload;
        IsMedia = download is MediaDownload;
        Tick();
    }

    public IDownload Download { get; }
    public bool IsCard { get; }
    public double ContentWidth => IsCard ? 396 : 596;
    public string Glyph { get; }
    public IBrush TileBrush { get; }
    public string Host { get; }
    public bool IsHttp { get; }
    public bool IsTorrent { get; }
    public bool IsMedia { get; }
    /// First visible tab: connections (HTTP), peers (torrent), else the speed graph. A hidden tab stays selectable.
    public int FirstTab => IsHttp ? 0 : IsTorrent ? 1 : 2;

    public ObservableCollection<ConnectionRow> Connections { get; } = [];
    public ObservableCollection<PeerRow> Peers { get; } = [];

    /// Raised once when the download completes (the window applies "when done" options).
    public event Action? Completed;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private IBrush _statusBrush = Brushes.Gray;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _avgSpeedText = "—";
    [ObservableProperty] private string _elapsedText = "—";
    [ObservableProperty] private string _connectionsText = "—";
    [ObservableProperty] private string _resumableText = "—";
    [ObservableProperty] private string _serverText = "—";
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _savePath = "";
    [ObservableProperty] private SegmentRow[] _segments = [];
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double[] _speedHistory = [];
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _canRefreshLink;
    [ObservableProperty] private string _toggleText = "";
    [ObservableProperty] private string _toggleGlyph = "";
    [ObservableProperty] private bool _compact;
    [ObservableProperty] private bool _openFileWhenDone;
    [ObservableProperty] private bool _openFolderWhenDone;
    [ObservableProperty] private bool _closeWhenDone;
    [ObservableProperty] private string _summaryText = "";

    public void Tick()
    {
        var d = Download;
        var r = d.Record;
        var p = d.GetProgress();
        var state = d.State;

        Name = d.FileName.Length > 0 ? d.FileName : r.Http?.Url ?? r.Media?.PageUrl ?? "…";
        IsRunning = state is DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.Seeding;
        IsCompleted = state == DownloadState.Completed;
        IsFailed = state is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh;
        CanRefreshLink = state == DownloadState.NeedsLinkRefresh && d is HttpDownload;
        ErrorText = Loc.Error(r.Error);
        StatusText = Loc.State(state);
        StatusBrush = IsRunning && state != DownloadState.Seeding ? AccentBrush()
            : new SolidColorBrush(Color.Parse(IsFailed ? "#E5484D" : IsCompleted ? "#30A46C" : state == DownloadState.Seeding ? "#8E4EC6" : "#8A8A8E"));
        var canPause = IsRunning || state == DownloadState.Queued;
        ToggleText = canPause ? Strings.Pause : Strings.Resume;
        ToggleGlyph = canPause ? "" : "";

        var fraction = IsCompleted ? 1 : p.Fraction;
        Percent = fraction * 100;
        Indeterminate = IsRunning && p.TotalBytes is null or 0;
        PercentText = p.TotalBytes is > 0 || IsCompleted ? Loc.Percent(fraction, fraction >= 0.995 ? 0 : 1) : "—";
        DoneText = p.TotalBytes is > 0 ? $"{Loc.Bytes(p.BytesDone)} / {Loc.Bytes(p.TotalBytes)}" : Loc.Bytes(p.BytesDone);
        SizeText = Loc.Bytes(p.TotalBytes ?? p.BytesDone);
        SpeedText = state == DownloadState.Downloading || state == DownloadState.Seeding ? Loc.Speed(p.BytesPerSecond) : "";
        EtaText = state == DownloadState.Downloading && p.Eta is { } eta ? string.Format(CultureInfo.CurrentCulture, Strings.EtaLeft, Loc.Eta(eta)) : "";
        SavePath = r.FullPath;
        TotalBytes = p.TotalBytes ?? 0;
        Title = IsCompleted ? Name : $"{PercentText} · {Name}";

        // Average speed and elapsed time are measured while this window watches the download.
        if (state == DownloadState.Downloading)
        {
            if (!_elapsed.IsRunning) _elapsed.Start();
            if (_bytesAtStart < 0) _bytesAtStart = p.BytesDone;
        }
        else _elapsed.Stop();
        if (_elapsed.Elapsed.TotalSeconds >= 1 && _bytesAtStart >= 0)
        {
            ElapsedText = Loc.Eta(_elapsed.Elapsed);
            AvgSpeedText = Loc.Speed((long)((p.BytesDone - _bytesAtStart) / _elapsed.Elapsed.TotalSeconds));
        }

        _history.Enqueue(state == DownloadState.Downloading || state == DownloadState.Seeding ? p.BytesPerSecond : 0);
        while (_history.Count > HistoryLength) _history.Dequeue();
        SpeedHistory = [.. _history];

        switch (d)
        {
            case HttpDownload hd:
                ResumableText = r.Http!.Resumable ? Strings.Yes : Strings.No;
                ServerText = hd.ResponseHeaders.FirstOrDefault(h => h.Key.Equals("Server", StringComparison.OrdinalIgnoreCase)).Value ?? Host;
                Segments = hd.GetSegments();
                UpdateConnections(hd);
                ConnectionsText = $"{p.Connections} / {r.Http.MaxConnections}";
                break;
            case TorrentDownload td:
                ResumableText = Strings.Yes;
                ServerText = string.Format(CultureInfo.CurrentCulture, Strings.TorrentUpStat, Loc.Speed(td.UploadRate), Loc.Bytes(td.UploadedBytes));
                ConnectionsText = string.Format(CultureInfo.CurrentCulture, Strings.PeersCount, p.Connections);
                if (++_slow % 4 == 0) _ = RefreshPeersAsync(td);
                break;
            case MediaDownload md:
                var (i, n) = md.Fragments;
                ConnectionsText = n > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.FragmentFormat, i, n) : "yt-dlp";
                ResumableText = Strings.Yes;
                ServerText = Host;
                break;
        }

        if (IsCompleted)
        {
            var parts = new List<string> { SizeText };
            if (AvgSpeedText != "—") parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.AvgSpeedIs, AvgSpeedText));
            if (ElapsedText != "—") parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.TookIs, ElapsedText));
            SummaryText = string.Join("  ·  ", parts);
        }
        if (_lastState != DownloadState.Completed && state == DownloadState.Completed) Completed?.Invoke();
        _lastState = state;
    }

    /// Follows the accent colour the user picked (or the Windows one).
    private static IBrush AccentBrush() =>
        Avalonia.Application.Current?.TryFindResource("SystemAccentColor", Avalonia.Application.Current.ActualThemeVariant, out var c) == true && c is Color col
            ? new SolidColorBrush(col) : new SolidColorBrush(Color.Parse("#3B82F6"));

    private void UpdateConnections(HttpDownload hd)
    {
        var rows = hd.GetSegments();
        var active = hd.GetActiveSegments();
        var now = Stopwatch.GetTimestamp();
        foreach (var s in rows)
        {
            var row = Connections.FirstOrDefault(c => c.Index == s.Index);
            if (row is null)
            {
                row = new ConnectionRow(s.Index);
                var at = 0;
                while (at < Connections.Count && Connections[at].Index < s.Index) at++;
                Connections.Insert(at, row);
            }
            var len = Math.Max(1, s.End - s.Start);
            var got = Math.Min(s.Position, s.End) - s.Start;
            row.Range = $"{Loc.Bytes(s.Start)} – {Loc.Bytes(s.End)}";
            row.Done = Loc.Bytes(got);
            row.Percent = got * 100.0 / len;
            row.IsActive = active.Contains(s.Index);
            var done = s.Position >= s.End;
            row.State = done ? Strings.ConnDone : row.IsActive ? Strings.ConnActive : Strings.ConnWaiting;
            if (row.LastPosition >= 0 && row.IsActive)
            {
                var dt = Stopwatch.GetElapsedTime(row.LastTicks, now).TotalSeconds;
                if (dt >= 0.2) row.Speed = Loc.Speed((long)((s.Position - row.LastPosition) / dt));
            }
            else if (!row.IsActive) row.Speed = "";
            if (row.LastPosition < 0 || Stopwatch.GetElapsedTime(row.LastTicks, now).TotalSeconds >= 0.2)
            {
                row.LastPosition = s.Position;
                row.LastTicks = now;
            }
        }
    }

    private async Task RefreshPeersAsync(TorrentDownload td)
    {
        var peers = await td.GetPeersAsync();
        Peers.Clear();
        foreach (var peer in peers.OrderByDescending(p => p.Monitor.DownloadRate).Take(100))
            Peers.Add(new PeerRow(peer.Uri.ToString(), peer.ClientApp.Client.ToString(), Loc.Speed(peer.Monitor.DownloadRate),
                Loc.Speed(peer.Monitor.UploadRate), Loc.Percent(peer.BitField.PercentComplete / 100)));
    }

    // ================= commands =================

    [RelayCommand]
    private async Task Toggle()
    {
        var q = _host.Engine.Queue;
        if (q.IsRunning(Download.Id) || Download.State == DownloadState.Queued) await q.PauseAsync(Download.Id);
        else q.Resume(Download.Id);
        Tick();
    }

    /// Cancel = remove from the list; the user decides whether the partial file goes too.
    [RelayCommand]
    private async Task Cancel(Avalonia.Controls.Window? window)
    {
        var (ok, deleteFiles) = await Dialogs.ConfirmDeleteAsync(window, 1);
        if (!ok) return;
        await _host.Engine.Queue.RemoveAsync(Download.Id, deleteFiles);
        window?.Close();
    }

    [RelayCommand]
    private void OpenFile()
    {
        var path = Download.Record.FullPath;
        if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var path = Download.Record.FullPath;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        else if (Directory.Exists(Download.Record.SaveDirectory)) Process.Start("explorer.exe", $"\"{Download.Record.SaveDirectory}\"");
    }

    [RelayCommand]
    private Task RefreshLink(Avalonia.Controls.Window? window) =>
        Download is HttpDownload hd ? RefreshLinkCoreAsync(hd, window) : Task.CompletedTask;

    private async Task RefreshLinkCoreAsync(HttpDownload hd, Avalonia.Controls.Window? window)
    {
        var url = await Dialogs.InputAsync(window, Strings.RefreshLinkTitle, Strings.RefreshLinkPrompt, hd.Record.Http!.Url);
        if (!string.IsNullOrWhiteSpace(url)) await Flows.RefreshLinkAsync(_host, hd, url.Trim(), null);
    }

    [RelayCommand]
    private void ToggleCompact() => Compact = !Compact;
}

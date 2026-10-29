using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// One of the user's queues in Settings → Speed and schedule.
public sealed partial class QueueRow(string id, Action changed) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _startOn;
    [ObservableProperty] private TimeSpan? _start = new(9, 0, 0);
    [ObservableProperty] private bool _stopOn;
    [ObservableProperty] private TimeSpan? _stop = new(18, 0, 0);
    [ObservableProperty] private decimal? _slots;
    [ObservableProperty] private int _afterIndex;
    [ObservableProperty] private bool _mon = true;
    [ObservableProperty] private bool _tue = true;
    [ObservableProperty] private bool _wed = true;
    [ObservableProperty] private bool _thu = true;
    [ObservableProperty] private bool _fri = true;
    [ObservableProperty] private bool _sat = true;
    [ObservableProperty] private bool _sun = true;
    public bool Loading { get; set; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!Loading) changed();
    }

    public static QueueRow From(NamedQueue q, Action changed)
    {
        var r = new QueueRow(q.Id, changed) { Loading = true };
        r.Name = q.Name;
        r.StartOn = q.Start is not null;
        r.Start = Parse(q.Start) ?? new TimeSpan(9, 0, 0);
        r.StopOn = q.Stop is not null;
        r.Stop = Parse(q.Stop) ?? new TimeSpan(18, 0, 0);
        r.Slots = q.Slots;
        r.AfterIndex = q.AfterDone switch { "Sleep" => 1, "Shutdown" => 2, _ => 0 };
        var d = q.Days.Length == 7 ? q.Days : "1111111";
        (r.Mon, r.Tue, r.Wed, r.Thu, r.Fri, r.Sat, r.Sun) = (d[0] == '1', d[1] == '1', d[2] == '1', d[3] == '1', d[4] == '1', d[5] == '1', d[6] == '1');
        r.Loading = false;
        return r;
    }

    public NamedQueue ToQueue() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? Strings.QueueDefaultName : Name.Trim(),
        Start = StartOn && Start is { } a ? $"{a.Hours:00}:{a.Minutes:00}" : null,
        Stop = StopOn && Stop is { } b ? $"{b.Hours:00}:{b.Minutes:00}" : null,
        Days = string.Concat(new[] { Mon, Tue, Wed, Thu, Fri, Sat, Sun }.Select(on => on ? '1' : '0')),
        Slots = (int)Math.Clamp(Slots ?? 0, 0, 20),
        AfterDone = AfterIndex switch { 1 => "Sleep", 2 => "Shutdown", _ => "None" },
    };

    private static TimeSpan? Parse(string? t) =>
        TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v.ToTimeSpan() : null;
}

public sealed partial class SettingsViewModel
{
    public ObservableCollection<QueueRow> QueueRows { get; } = [];
    /// Monday first, in the UI language ("Pzt", "Sal"…).
    public string[] DayNames => [.. Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(i + 1) % 7])];

    private void LoadQueues(AppSettings s)
    {
        QueueRows.Clear();
        foreach (var q in s.Queues) QueueRows.Add(QueueRow.From(q, QueueSave));
    }

    [RelayCommand]
    private void AddQueue()
    {
        var name = string.Format(CultureInfo.CurrentCulture, Strings.QueueNumbered, QueueRows.Count + 1);
        QueueRows.Add(QueueRow.From(new NamedQueue { Name = name, Start = "09:00" }, QueueSave));
        QueueSave();
    }

    /// Its waiting downloads go back to the main queue (paused), so nothing is stuck in a queue that is gone.
    [RelayCommand]
    private async Task RemoveQueue(QueueRow row)
    {
        var waiting = _host.Engine.Queue.Items.Where(d => d.Record.QueueId == row.Id).ToList();
        if (waiting.Count > 0 && !await Dialogs.ConfirmAsync(_host.Owner, Strings.QueueRemove,
                string.Format(CultureInfo.CurrentCulture, Strings.QueueRemoveConfirm, row.Name, waiting.Count))) return;
        foreach (var d in waiting)
        {
            d.Record.QueueId = null;
            if (d.State == DownloadState.Scheduled) await _host.Engine.Queue.PauseAsync(d.Id);
            else _host.Engine.Queue.Save(d);
        }
        QueueRows.Remove(row);
        QueueSave();
    }

    [RelayCommand]
    private async Task StartQueueNow(QueueRow row)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); await SaveAsync(); }
        _host.Engine.Queue.StartQueue(row.Id);
    }
}

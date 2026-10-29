using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// One site in Settings → Sites.
public sealed partial class SiteRow(Action changed) : ObservableObject
{
    private const long MB = 1024 * 1024;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private decimal? _maxConnections;
    [ObservableProperty] private string _userAgent = "";
    [ObservableProperty] private string _referrer = "";
    [ObservableProperty] private decimal? _speedMb;
    public bool Loading { get; set; }
    // The stored blob is reused while the password is unchanged, so saving other settings does not re-encrypt it.
    private string? _blob;
    private string _loadedPassword = "";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!Loading) changed();
    }

    public static SiteRow From(SiteRule r, Action changed)
    {
        var row = new SiteRow(changed) { Loading = true, _blob = r.PasswordProtected };
        row.Host = r.Host;
        row.UserName = r.UserName ?? "";
        row.Password = row._loadedPassword = r.Password ?? "";
        row.MaxConnections = r.MaxConnections;
        row.UserAgent = r.UserAgent ?? "";
        row.Referrer = r.Referrer ?? "";
        row.SpeedMb = Math.Round((decimal)r.SpeedLimit / MB, 2);
        row.Loading = false;
        return row;
    }

    public SiteRule ToRule()
    {
        var rule = new SiteRule
        {
            Host = Host.Trim().ToLowerInvariant(),
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
            MaxConnections = (int)Math.Clamp(MaxConnections ?? 0, 0, 32),
            UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? null : UserAgent.Trim(),
            Referrer = string.IsNullOrWhiteSpace(Referrer) ? null : Referrer.Trim(),
            SpeedLimit = (long)((SpeedMb ?? 0) * MB),
        };
        if (Password == _loadedPassword) rule.PasswordProtected = _blob;
        else
        {
            rule.Password = Password.Length > 0 ? Password : null;
            (_blob, _loadedPassword) = (rule.PasswordProtected, Password);
        }
        return rule;
    }
}

public sealed partial class SettingsViewModel
{
    public ObservableCollection<SiteRow> SiteRows { get; } = [];

    // Smart speed (Settings → Speed)
    [ObservableProperty] private bool _busyOn;
    [ObservableProperty] private int _busyProfileIndex = 2;
    [ObservableProperty] private decimal? _idleMinutes = 3;
    [ObservableProperty] private bool _batteryOn;
    [ObservableProperty] private decimal? _batteryPercent = 20;
    [ObservableProperty] private bool _quotaOn;
    [ObservableProperty] private decimal? _quotaGb = 10;
    [ObservableProperty] private decimal? _quotaHours = 24;
    [ObservableProperty] private string _quotaUsedText = "";
    public string[] ProfileNames => [.. Profiles.Select(p => p.Name)];

    private void LoadSites(AppSettings s)
    {
        SiteRows.Clear();
        foreach (var r in s.Sites) SiteRows.Add(SiteRow.From(r, QueueSave));
        var smart = s.Smart;
        BusyOn = smart.BusyOn;
        BusyProfileIndex = Math.Clamp(smart.BusyProfile, 0, 2);
        IdleMinutes = smart.IdleMinutes;
        BatteryOn = smart.BatteryPercent > 0;
        BatteryPercent = smart.BatteryPercent > 0 ? smart.BatteryPercent : 20;
        QuotaOn = smart.QuotaMb > 0;
        QuotaGb = smart.QuotaMb > 0 ? Math.Round(smart.QuotaMb / 1024m, 1) : 10;
        QuotaHours = smart.QuotaHours;
        RefreshQuotaUsed();
    }

    private void SaveSites(AppSettings s)
    {
        s.Sites = [.. SiteRows.Select(r => r.ToRule()).Where(r => r.Host.Length > 0)];
        s.Smart = new SmartSpeed
        {
            BusyOn = BusyOn,
            BusyProfile = Math.Clamp(BusyProfileIndex, 0, 2),
            IdleMinutes = (int)Math.Clamp(IdleMinutes ?? 3, 1, 120),
            BatteryPercent = BatteryOn ? (int)Math.Clamp(BatteryPercent ?? 20, 5, 95) : 0,
            QuotaMb = QuotaOn ? (long)(Math.Clamp(QuotaGb ?? 10, 0.1m, 100_000) * 1024) : 0,
            QuotaHours = (int)Math.Clamp(QuotaHours ?? 24, 1, 24),
        };
    }

    /// "Last 24 hours: 1.2 GB" under the quota.
    private void RefreshQuotaUsed()
    {
        var hours = (int)Math.Clamp(QuotaHours ?? 24, 1, 24);
        var used = _host.Engine.Meter.DownWithin(TimeSpan.FromHours(hours), DateTimeOffset.UtcNow);
        QuotaUsedText = string.Format(CultureInfo.CurrentCulture, Strings.QuotaUsed, hours, Loc.Bytes(used));
    }

    partial void OnQuotaHoursChanged(decimal? value) => RefreshQuotaUsed();

    [RelayCommand]
    private void AddSite()
    {
        SiteRows.Add(SiteRow.From(new SiteRule(), QueueSave));
    }

    [RelayCommand]
    private void RemoveSite(SiteRow row)
    {
        SiteRows.Remove(row);
        QueueSave();
    }
}

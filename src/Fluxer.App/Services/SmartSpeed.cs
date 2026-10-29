using System.Globalization;
using Fluxer.Core.Platform.Windows;
using Serilog;

namespace Fluxer.App.Services;

/// Settings → Speed → Smart speed: a slower profile while someone works in another program, and a hold on every
/// download on low battery or once the traffic quota is used up. Checked on every tick (2 s).
public sealed partial class AppHost
{
    /// The profile to go back to once the user stops working in another program.
    private int? _busyBase;
    private readonly HashSet<string> _holds = [];

    private void RunSmartSpeed()
    {
        var now = DateTimeOffset.UtcNow;
        Engine.MeasureTraffic(now);
        var s = Engine.Settings;
        var smart = s.Smart;

        var busy = smart.BusyOn && smart.BusyProfile < s.Profiles.Count && Activity.OtherAppInFront()
            && Activity.Idle() < TimeSpan.FromMinutes(Math.Max(1, smart.IdleMinutes));
        if (busy && _busyBase is null && s.ActiveProfile != smart.BusyProfile)
        {
            _busyBase = s.ActiveProfile;
            Log.Information("Smart speed: busy → profile {Profile}", smart.BusyProfile);
            _ = UseProfileAsync(smart.BusyProfile);
        }
        else if (!busy && _busyBase is { } back)
        {
            _busyBase = null;
            // Unless the user or the weekly schedule picked another profile meanwhile.
            if (s.ActiveProfile == smart.BusyProfile) _ = UseProfileAsync(back);
        }

        // Low battery holds until mains power, not until the next percent up, so it does not flap.
        var battery = Activity.OnBatteryPercent();
        Hold("battery", smart.BatteryPercent > 0 && battery is { } pct && (pct < smart.BatteryPercent || _holds.Contains("battery")),
            () => string.Format(CultureInfo.CurrentCulture, Strings.HoldBatteryBody, battery));

        var window = TimeSpan.FromHours(Math.Clamp(smart.QuotaHours, 1, 24));
        Hold("quota", smart.QuotaMb > 0 && Engine.Meter.DownWithin(window, now) >= smart.QuotaMb * 1024 * 1024,
            () => string.Format(CultureInfo.CurrentCulture, Strings.HoldQuotaBody, QuotaFreesText(window, now)));

        Main.HoldText = string.Join(" · ", _holds.Order().Select(h => h == "battery" ? Strings.HoldBattery
            : string.Format(CultureInfo.CurrentCulture, Strings.HoldQuota, QuotaFreesText(window, now))));
    }

    private string QuotaFreesText(TimeSpan window, DateTimeOffset now) =>
        Engine.Meter.WindowFreesAt(window, now) is { } at ? at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : "";

    private void Hold(string reason, bool on, Func<string> body)
    {
        if (on == _holds.Contains(reason)) return;
        if (on)
        {
            _holds.Add(reason);
            if (Engine.Settings.Notifications) Notifier.Show(Strings.HoldTitle, body());
        }
        else _holds.Remove(reason);
        Log.Information("Smart speed: {Reason} hold {State}", reason, on ? "on" : "off");
        _ = Engine.Queue.HoldAsync(_holds.Count > 0);
    }

    /// Quitting while busy: the usual profile is what the next start should find.
    private async Task RestoreBusyProfileAsync()
    {
        var s = Engine.Settings;
        if (_busyBase is { } back && s.ActiveProfile == s.Smart.BusyProfile)
        {
            s.UseProfile(back);
            await Engine.SaveSettingsAsync(s);
        }
        _busyBase = null;
    }
}

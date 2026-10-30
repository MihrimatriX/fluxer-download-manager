using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Views;
using Fluxer.Core.Plugins;

namespace Fluxer.App.ViewModels;

/// One plugin file in Settings → Plugins.
public sealed partial class PluginRow(PluginHost.Plugin plugin, Func<PluginRow, bool, Task> toggled) : ObservableObject
{
    public PluginHost.Plugin Plugin { get; } = plugin;
    public string Name => Plugin.Manifest.Name;
    public string Version => Plugin.Manifest.Version.Length > 0 ? "v" + Plugin.Manifest.Version : "";
    public string Description => Plugin.Manifest.Description;
    public bool HasDescription => Description.Length > 0;
    public string File => Plugin.File;
    public string[] Permissions => Plugin.Manifest.Permissions.Count > 0
        ? [.. Plugin.Manifest.Permissions.Select(SettingsViewModel.PermissionShort)]
        : [Strings.PluginNoPermissions];
    [ObservableProperty] private bool _enabled = plugin.Enabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = plugin.Error ?? "";
    public bool HasError => Error.Length > 0;
    public bool Loading { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (!Loading) _ = toggled(this, value);
    }

    public void Refresh()
    {
        Loading = true;
        Enabled = Plugin.Enabled;
        Error = Plugin.Error ?? "";
        Loading = false;
    }
}

public sealed partial class SettingsViewModel
{
    public ObservableCollection<PluginRow> PluginRows { get; } = [];

    public static string PermissionShort(string p) => p switch
    {
        "notify" => Strings.PermNotifyShort,
        "downloads" => Strings.PermDownloadsShort,
        "http" => Strings.PermHttpShort,
        _ => p,
    };

    private static string PermissionLong(string p) => p switch
    {
        "notify" => Strings.PermNotify,
        "downloads" => Strings.PermDownloads,
        "http" => Strings.PermHttp,
        _ => p,
    };

    /// After a reload or a plugin error.
    public void RefreshPlugins()
    {
        var plugins = _host.Plugins.Plugins;
        if (PluginRows.Select(r => r.Plugin).SequenceEqual(plugins)) { foreach (var r in PluginRows) r.Refresh(); return; }
        PluginRows.Clear();
        foreach (var p in plugins) PluginRows.Add(new PluginRow(p, TogglePluginAsync));
    }

    /// Turning one on is a decision about what it may do, so the permissions are spelled out first.
    private async Task TogglePluginAsync(PluginRow row, bool on)
    {
        var s = _host.Engine.Settings;
        if (on)
        {
            var p = row.Plugin.Manifest;
            var text = p.Permissions.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.PluginEnableConfirm, p.Name, string.Join("; ", p.Permissions.Select(PermissionLong)))
                : string.Format(CultureInfo.CurrentCulture, Strings.PluginEnableNoPerms, p.Name);
            if (!await Dialogs.ConfirmAsync(_host.Owner, Strings.PluginEnable, text))
            {
                row.Refresh();
                return;
            }
            s.Plugins.RemoveAll(g => string.Equals(g.File, row.File, StringComparison.OrdinalIgnoreCase));
            s.Plugins.Add(new PluginGrant { File = row.File, Hash = row.Plugin.Hash });
        }
        else s.Plugins.RemoveAll(g => string.Equals(g.File, row.File, StringComparison.OrdinalIgnoreCase));
        await _host.Engine.SaveSettingsAsync(s);
        await ReloadPlugins();
    }

    [RelayCommand]
    private async Task ReloadPlugins()
    {
        await _host.ReloadPlugins();
        RefreshPlugins();
    }

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        Directory.CreateDirectory(_host.Plugins.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.Plugins.Folder}\"") { UseShellExecute = false })?.Dispose();
    }

    [RelayCommand]
    private async Task AddExamplePlugin()
    {
        _host.Plugins.WriteExample();
        await ReloadPlugins();
    }
}

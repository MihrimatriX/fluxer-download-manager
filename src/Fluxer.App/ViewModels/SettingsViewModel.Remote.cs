using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// Settings → Automation → Remote access.
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _remoteOn;
    [ObservableProperty] private decimal? _remotePort = 6800;
    [ObservableProperty] private bool _remoteLan;
    [ObservableProperty] private string _remoteStatus = "";
    [ObservableProperty] private string _remoteNote = "";

    private void LoadRemote(AppSettings s)
    {
        RemoteOn = s.Remote.On;
        RemotePort = s.Remote.Port;
        RemoteLan = s.Remote.Lan;
        RefreshRemote();
    }

    private void RefreshRemote()
    {
        RemoteStatus = _host.RemoteStatus;
        RemoteNote = _host.RemotePort > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.RemoteAria2Note, _host.RemotePort) : "";
    }

    [RelayCommand]
    private async Task CopyRemoteKey()
    {
        await Dialogs.SetClipboardTextAsync(_host.Owner, _host.RemoteKey());
        RemoteNote = Strings.RemoteKeyCopied;
    }

    [RelayCommand]
    private async Task NewRemoteKey()
    {
        if (!await Dialogs.ConfirmAsync(_host.Owner, Strings.RemoteNewKey, Strings.RemoteNewKeyConfirm)) return;
        await _host.NewRemoteKeyAsync();
        RefreshRemote();
    }

    [RelayCommand]
    private void OpenRemotePage() => _host.OpenRemotePage();
}

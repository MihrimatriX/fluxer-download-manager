using Avalonia.Threading;
using Fluxer.Core.Plugins;

namespace Fluxer.App.Services;

/// JavaScript plugins (Fluxer.Core.Plugins): loaded off the UI thread, told about download events.
public sealed partial class AppHost
{
    public PluginHost Plugins { get; private set; } = null!;

    /// Before the queue loads, so an event never finds no host; the scripts themselves load later.
    private void CreatePlugins() => Plugins = new PluginHost(Path.Combine(Program.DataDirectory, "plugins"), Engine, Notifier.Show);

    private void StartPlugins()
    {
        Plugins.Faulted += _ => Dispatcher.UIThread.Post(() => Main.Settings.RefreshPlugins());
        ReloadPlugins();
    }

    /// A plugin's top level may take a moment (or a web request); the window does not wait for it.
    public Task ReloadPlugins()
    {
        var grants = Engine.Settings.Plugins.ToList();
        return Task.Run(() => Plugins.Load(grants));
    }
}

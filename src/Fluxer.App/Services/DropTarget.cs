using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fluxer.App.Views;

namespace Fluxer.App.Services;

/// Settings → General → Drop target; also in the tray menu.
public sealed partial class AppHost
{
    private DropTargetWindow? _drop;

    public void ApplyDropTarget()
    {
        var ui = Engine.Settings.Ui;
        if (!ui.DropTarget)
        {
            SaveDropTargetPosition();
            _drop?.Close();
            _drop = null;
            return;
        }
        if (_drop is not null) return;
        _drop = new DropTargetWindow(Main, ShowMainWindow, () => _ = SetDropTargetAsync(false));
        if (ui.DropTargetPosition is [var x, var y]) _drop.Position = new PixelPoint(x, y);
        else
        {
            _drop.WindowStartupLocation = WindowStartupLocation.Manual;
            _drop.Opened += (_, _) =>
            {
                // Bottom right of the screen it opened on, above the taskbar.
                if (_drop?.Screens.ScreenFromWindow(_drop) is { } screen)
                    _drop.Position = new PixelPoint(screen.WorkingArea.Right - (int)(96 * screen.Scaling), screen.WorkingArea.Bottom - (int)(120 * screen.Scaling));
            };
        }
        // Remembered a moment after it stops moving.
        _dropSave ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _dropSave!.Stop();
            if (SaveDropTargetPosition()) _ = Engine.SaveSettingsAsync(Engine.Settings);
        });
        _drop.PositionChanged += (_, _) => { _dropMoved = true; _dropSave.Stop(); _dropSave.Start(); };
        _drop.Show();
    }

    private bool _dropMoved;
    private DispatcherTimer? _dropSave;

    /// True when there was a new position to keep (the caller saves the settings).
    private bool SaveDropTargetPosition()
    {
        if (_drop is null || !_dropMoved) return false;
        Engine.Settings.Ui.DropTargetPosition = [_drop.Position.X, _drop.Position.Y];
        _dropMoved = false;
        return true;
    }

    public void RebuildTray() => BuildTray();

    public async Task SetDropTargetAsync(bool on)
    {
        SaveDropTargetPosition();
        Engine.Settings.Ui.DropTarget = on;
        await Engine.SaveSettingsAsync(Engine.Settings);
        ApplyDropTarget();
        BuildTray();
        Main.Settings.Load();
    }

    private void UpdateDropTarget(long speed)
    {
        if (_drop is null) return;
        _drop.Update(speed > 0 ? Loc.Speed(speed) : "");
    }
}

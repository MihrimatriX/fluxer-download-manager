using System.Globalization;
using System.Text.Json;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Services;
using Fluxer.App.Views;
using Fluxer.Core;
using Fluxer.Core.Downloads;
using Fluxer.Core.Storage;

namespace Fluxer.App.ViewModels;

/// Settings → Advanced → Data: where the data lives (portable mode), list and settings export/import, backups.
public sealed partial class SettingsViewModel
{
    public string[] BackupIntervals => [Strings.BackupOff, Strings.BackupDaily, Strings.BackupWeekly];
    [ObservableProperty] private int _backupIndex;
    [ObservableProperty] private string _backupText = "";
    [ObservableProperty] private string _dataText = "";
    public string DataFolder => Program.DataDirectory;
    public bool CanSwitchPortable => PortableData.CanSwitch;
    public string PortableButton => PortableData.IsPortable ? Strings.PortableOff : Strings.PortableOn;

    private void LoadData(AppSettings s)
    {
        BackupIndex = s.BackupInterval switch { "off" => 0, "weekly" => 2, _ => 1 };
        RefreshBackupText();
        DataText = "";
    }

    private static string BackupSetting(int index) => index switch { 0 => "off", 2 => "weekly", _ => "daily" };

    private void RefreshBackupText()
    {
        var list = Backups.List(Program.DataDirectory);
        BackupText = list.Count == 0 ? Strings.BackupNone
            : string.Format(CultureInfo.CurrentCulture, Strings.BackupLast, Loc.When(list[0].LastWriteTime), list.Count);
    }

    [RelayCommand]
    private async Task BackupNow()
    {
        BackupText = Strings.BackupRunning;
        await _host.BackupNowAsync();
        RefreshBackupText();
    }

    [RelayCommand]
    private async Task RestoreBackup()
    {
        if (_host.Owner is not { } w) return;
        Directory.CreateDirectory(Backups.Folder(Program.DataDirectory));
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.BackupRestore,
            FileTypeFilter = [new FilePickerFileType("Fluxer") { Patterns = ["*.db"] }],
            SuggestedStartLocation = await w.StorageProvider.TryGetFolderFromPathAsync(Backups.Folder(Program.DataDirectory)),
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (!await Dialogs.ConfirmAsync(w, Strings.BackupRestore, string.Format(CultureInfo.CurrentCulture, Strings.BackupRestoreConfirm, Path.GetFileName(path)),
                Strings.RestartNow, Strings.Cancel)) return;
        PortableData.StageRestore(Program.DataDirectory, path);
        await _host.RestartAsync();
    }

    [RelayCommand]
    private async Task TogglePortable()
    {
        var on = !PortableData.IsPortable;
        var target = on ? PortableData.PortableDirectory : PortableData.LocalDirectory;
        if (!await Dialogs.ConfirmAsync(_host.Owner, Strings.PortableMode, string.Format(CultureInfo.CurrentCulture, on ? Strings.PortableOnConfirm : Strings.PortableOffConfirm, target),
                Strings.RestartNow, Strings.Cancel)) return;
        try
        {
            await Task.Run(() => PortableData.SwitchTo(on, _host.Engine.Db, Program.DataDirectory));
        }
        catch (Exception e)
        {
            await Dialogs.MessageAsync(_host.Owner, Strings.PortableMode, Loc.Error(e.Message));
            return;
        }
        await _host.RestartAsync();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (Directory.Exists(Program.DataDirectory)) System.Diagnostics.Process.Start("explorer.exe", $"\"{Program.DataDirectory}\"")?.Dispose();
    }

    [RelayCommand]
    private async Task ExportList()
    {
        if (_host.Owner is not { } w) return;
        var json = new FilePickerFileType(Strings.ExportJson) { Patterns = ["*.json"] };
        var text = new FilePickerFileType(Strings.ExportText) { Patterns = ["*.txt"] };
        var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"fluxer-downloads-{DateTime.Now:yyyyMMdd}",
            DefaultExtension = "json",
            FileTypeChoices = [json, text],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var records = _host.Engine.Queue.Items.OrderBy(d => d.Record.QueueOrder).Select(d => d.Record).ToList();
        var asText = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(path, asText ? ListTransfer.ToText(records) : ListTransfer.ToJson(records));
        DataText = string.Format(CultureInfo.CurrentCulture, Strings.ExportDone, records.Count, Path.GetFileName(path));
    }

    /// A Fluxer export comes back as it was (paused, finished ones as history); a text file of links opens the
    /// new-download window with them.
    [RelayCommand]
    private async Task ImportList()
    {
        if (_host.Owner is not { } w) return;
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.ImportList,
            FileTypeFilter = [new FilePickerFileType(Strings.ImportList) { Patterns = ["*.json", "*.txt"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        var content = await File.ReadAllTextAsync(path);
        if (ListTransfer.ParseExport(content) is { } export)
        {
            var records = export.Items.Select(ListTransfer.ToRecord).OfType<DownloadRecord>().ToList();
            var (added, skipped) = _host.Engine.Import(records);
            DataText = string.Format(CultureInfo.CurrentCulture, Strings.ImportDone, added, skipped + export.Items.Count - records.Count);
            return;
        }
        var links = ListTransfer.ParseLinks(content);
        if (links.Count == 0) { DataText = Strings.ImportNothing; return; }
        DataText = "";
        await Flows.NewDownloadAsync(_host, links, null);
    }

    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private async Task ExportSettings()
    {
        if (_host.Owner is not { } w) return;
        var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"fluxer-settings-{DateTime.Now:yyyyMMdd}.json",
            DefaultExtension = "json",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(_host.Engine.Settings, new JsonSerializerOptions { WriteIndented = true }));
        DataText = string.Format(CultureInfo.CurrentCulture, Strings.SettingsExported, Path.GetFileName(path));
    }

    /// Replaces every setting except this window's size and position.
    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private async Task ImportSettings()
    {
        if (_host.Owner is not { } w) return;
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.ImportSettings,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        AppSettings? s;
        try { s = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path)); }
        catch (JsonException) { s = null; }
        if (s is null || s.Profiles.Count == 0) { DataText = Strings.ImportSettingsInvalid; return; }
        if (!await Dialogs.ConfirmAsync(w, Strings.ImportSettings, Strings.ImportSettingsConfirm)) return;
        var old = _host.Engine.Settings;
        s.Ui.WindowBounds = old.Ui.WindowBounds;
        s.Ui.WindowMaximized = old.Ui.WindowMaximized;
        s.UseProfile(s.ActiveProfile);
        s.UpgradeDesign();
        await _host.Engine.SaveSettingsAsync(s);
        Load();
        _host.ApplyWatchers();
        _host.Engine.Rss.Reschedule();
        _host.ReloadUi();
    }
}

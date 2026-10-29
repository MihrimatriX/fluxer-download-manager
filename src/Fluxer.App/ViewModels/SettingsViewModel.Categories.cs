using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// One category in Settings → Categories. The name is fixed once created (downloads refer to it).
public sealed partial class CategoryRow(string key, Action changed) : ObservableObject
{
    public string Key { get; } = key;
    public string Title => Cats.Title(Key);
    public Avalonia.Media.Geometry? Icon => Theme.Geometry(Cats.IconKey(Key));
    public Avalonia.Media.IBrush Tint => Theme.Brush(Cats.TintKey(Key));
    public bool IsCustom => !Category.BuiltIn.Contains(Key);
    public bool IsTorrent => Key == "Torrent";
    public bool HasRules => !IsTorrent;
    [ObservableProperty] private string _extensionsText = "";
    [ObservableProperty] private string _sitesText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FolderText), nameof(HasOwnFolder))] private string _folder = "";
    public bool HasOwnFolder => Folder.Length > 0;
    public string FolderText => HasOwnFolder ? Folder : string.Format(CultureInfo.CurrentCulture, Strings.CategoryFolderDefault, Key);
    public bool Loading { get; set; }

    partial void OnExtensionsTextChanged(string value) { if (!Loading) changed(); }
    partial void OnSitesTextChanged(string value) { if (!Loading) changed(); }
    partial void OnFolderChanged(string value) { if (!Loading) changed(); }

    public static CategoryRow From(Category c, Action changed)
    {
        var row = new CategoryRow(c.Key, changed) { Loading = true };
        row.ExtensionsText = string.Join(" ", c.Extensions);
        row.SitesText = string.Join(" ", c.Sites);
        row.Folder = c.Folder ?? "";
        row.Loading = false;
        return row;
    }

    public Category ToCategory() => new()
    {
        Key = Key,
        Extensions = IsTorrent ? [] : Category.ParseList(ExtensionsText, extensions: true),
        Sites = IsTorrent ? [] : Category.ParseList(SitesText, extensions: false),
        Folder = HasOwnFolder ? Folder.Trim() : null,
    };
}

public sealed partial class SettingsViewModel
{
    public ObservableCollection<CategoryRow> CategoryRows { get; } = [];

    private void LoadCategories(AppSettings s)
    {
        CategoryRows.Clear();
        foreach (var c in s.CategoryList) CategoryRows.Add(CategoryRow.From(c, QueueSave));
    }

    [RelayCommand]
    private async Task AddCategory()
    {
        var name = (await Dialogs.InputAsync(_host.Owner, Strings.CategoryAdd, Strings.CategoryName))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (name.StartsWith('#') || CategoryRows.Any(r => string.Equals(r.Key, name, StringComparison.CurrentCultureIgnoreCase)) || name == "all")
        {
            await Dialogs.MessageAsync(_host.Owner, Strings.CategoryAdd, Strings.CategoryExists);
            return;
        }
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));   // it is also a folder name
        if (name.Length == 0) return;
        CategoryRows.Insert(Math.Max(0, CategoryRows.Count - 1), CategoryRow.From(new Category { Key = name }, QueueSave));
        QueueSave();
    }

    [RelayCommand]
    private void RemoveCategory(CategoryRow row)
    {
        if (!row.IsCustom) return;
        CategoryRows.Remove(row);
        QueueSave();
    }

    [RelayCommand]
    private async Task BrowseCategoryFolder(CategoryRow row)
    {
        var start = row.HasOwnFolder ? row.Folder : Path.Combine(DownloadDirectory, row.Key);
        if (_host.Owner is { } w && await Dialogs.PickFolderAsync(w, Directory.Exists(start) ? start : DownloadDirectory) is { } path) row.Folder = path;
    }

    [RelayCommand]
    private static void ClearCategoryFolder(CategoryRow row) => row.Folder = "";

    [RelayCommand]
    private async Task ResetCategories()
    {
        if (!await Dialogs.ConfirmAsync(_host.Owner, Strings.CategoriesReset, Strings.CategoriesResetConfirm)) return;
        CategoryRows.Clear();
        foreach (var c in Category.Defaults()) CategoryRows.Add(CategoryRow.From(c, QueueSave));
        QueueSave();
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// Settings → Downloads → Archives.
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _extractAuto;
    [ObservableProperty] private bool _extractDeleteAfter;
    /// One password per line.
    [ObservableProperty] private string _extractPasswords = "";
    // Re-encrypted only when the text changes, like site passwords.
    private string _loadedExtractPasswords = "";
    private string? _extractBlob;

    private void LoadArchives(AppSettings s)
    {
        ExtractAuto = s.Extract.Auto;
        ExtractDeleteAfter = s.Extract.DeleteAfter;
        _extractBlob = s.Extract.PasswordsProtected;
        ExtractPasswords = _loadedExtractPasswords = string.Join(Environment.NewLine, s.Extract.Passwords);
    }

    private void SaveArchives(AppSettings s)
    {
        s.Extract = new ExtractSettings { Auto = ExtractAuto, DeleteAfter = ExtractDeleteAfter };
        if (ExtractPasswords == _loadedExtractPasswords) s.Extract.PasswordsProtected = _extractBlob;
        else
        {
            s.Extract.Passwords = [.. ExtractPasswords.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            (_extractBlob, _loadedExtractPasswords) = (s.Extract.PasswordsProtected, ExtractPasswords);
        }
    }
}

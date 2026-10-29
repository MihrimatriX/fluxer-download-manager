using System.Security.Cryptography;
using System.Text;

namespace Fluxer.Core.Platform.Windows;

public static class Dpapi
{
    public static byte[]? Protect(string? text) =>
        string.IsNullOrEmpty(text) ? null : ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser);

    /// A secret kept in the settings: encrypted for this Windows user, as base64; null for empty text.
    public static string? ProtectToBase64(string? text) => Protect(text) is { } blob ? Convert.ToBase64String(blob) : null;

    /// Null when nothing is stored, or it was stored by another account or machine, or the file was edited by hand.
    public static string? UnprotectBase64(string? stored)
    {
        if (stored is not { Length: > 0 }) return null;
        try { return Unprotect(Convert.FromBase64String(stored)); }
        catch (FormatException) { return null; }
    }

    public static string? Unprotect(byte[]? blob)
    {
        if (blob is null) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; } // DB copied from another user/machine
    }
}

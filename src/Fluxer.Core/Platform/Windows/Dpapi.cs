using System.Security.Cryptography;
using System.Text;

namespace Fluxer.Core.Platform.Windows;

public static class Dpapi
{
    public static byte[]? Protect(string? text) =>
        string.IsNullOrEmpty(text) ? null : ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser);

    public static string? Unprotect(byte[]? blob)
    {
        if (blob is null) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; } // DB copied from another user/machine
    }
}

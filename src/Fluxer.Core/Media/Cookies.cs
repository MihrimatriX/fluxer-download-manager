using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Fluxer.Core.Media;

public sealed record CookieEntry(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, double? Expires);

public static class Cookies
{
    /// "a=b; c=d" for the Cookie request header.
    public static string ToHeader(IEnumerable<CookieEntry> cookies) => string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

    /// Writes a "cookies-*.txt" file whose ACL grants only the current user; caller deletes it.
    public static string WritePrivateFile(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, $"cookies-{Guid.NewGuid():N}.txt"));
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        using (var fs = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
        using (var w = new StreamWriter(fs))
            w.Write(content);
        return path;
    }

    /// Netscape cookies.txt, the format yt-dlp's --cookies expects.
    public static string ToNetscape(IEnumerable<CookieEntry> cookies)
    {
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in cookies)
        {
            var domain = (c.HttpOnly ? "#HttpOnly_" : "") + c.Domain;
            var sub = c.Domain.StartsWith('.') ? "TRUE" : "FALSE";
            var expires = ((long)(c.Expires ?? 0)).ToString(CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $"{domain}\t{sub}\t{c.Path}\t{(c.Secure ? "TRUE" : "FALSE")}\t{expires}\t{c.Name}\t{c.Value}\n");
        }
        return sb.ToString();
    }
}

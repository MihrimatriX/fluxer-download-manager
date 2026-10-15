using System.Globalization;
using System.Text;

namespace Fluxer.Core.Media;

public sealed record CookieEntry(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, double? Expires);

public static class Cookies
{
    /// "a=b; c=d" for the Cookie request header.
    public static string ToHeader(IEnumerable<CookieEntry> cookies) => string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

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

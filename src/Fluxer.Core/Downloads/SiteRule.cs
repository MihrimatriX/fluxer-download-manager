using System.Text.Json.Serialization;
using Fluxer.Core.Platform.Windows;

namespace Fluxer.Core.Downloads;

/// Settings for one site, applied to every request that goes there: a login (HTTP Basic), how many connections
/// it tolerates, a User-Agent or Referer it wants, and a speed limit. The password is stored encrypted for the
/// Windows user (DPAPI), so a settings export or a copied database is of no use on another account.
public sealed class SiteRule
{
    /// "example.org" (also its subdomains) or "*.example.org".
    public string Host { get; set; } = "";
    public string? UserName { get; set; }
    public string? PasswordProtected { get; set; }      // base64 DPAPI blob
    public int MaxConnections { get; set; }             // 0 = the download's own number
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public long SpeedLimit { get; set; }                // B/s, 0 = none

    [JsonIgnore]
    public string? Password
    {
        get
        {
            if (PasswordProtected is not { Length: > 0 } p) return null;
            try { return Dpapi.Unprotect(Convert.FromBase64String(p)); }
            catch (FormatException) { return null; }    // a hand-edited settings file
        }
        set => PasswordProtected = Dpapi.Protect(value) is { } blob ? Convert.ToBase64String(blob) : null;
    }

    public bool Matches(string host) => Category.HostMatches(host, Host);

    /// "Basic dXNlcjpwYXNz" when a login is set.
    public string? Authorization() =>
        UserName is { Length: > 0 } u ? "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{u}:{Password}")) : null;
}

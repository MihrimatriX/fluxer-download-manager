namespace Fluxer.Core.Torrent;

/// Extra trackers for public torrents: the user's own lines plus a subscribed list (such as ngosang/trackerslist,
/// one address per line), refreshed once a day. Private torrents never get them: their tracker counts what each
/// member shares, and an extra tracker would leak the swarm.
public static class TrackerList
{
    public const int Max = 80;
    public const string Suggested = "https://cdn.jsdelivr.net/gh/ngosang/trackerslist@master/trackers_best.txt";

    /// Valid udp/http(s)/ws(s) announce addresses, first occurrence kept, at most Max.
    public static List<string> Parse(string? text) =>
        [.. (text ?? "").Split(['\r', '\n', ' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(IsTracker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Max)];

    public static bool IsTracker(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "udp" or "http" or "https" or "ws" or "wss" && u.Host.Length > 0;

    public static async Task<List<string>> FetchAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var text = await http.GetStringAsync(url, cts.Token);
        return Parse(text.Length > 1_000_000 ? text[..1_000_000] : text);
    }

    /// The user's own trackers first, then the subscribed ones, without duplicates.
    public static List<string> Merge(IEnumerable<string> own, IEnumerable<string> subscribed) =>
        [.. own.Concat(subscribed).Where(IsTracker).Distinct(StringComparer.OrdinalIgnoreCase).Take(Max)];
}

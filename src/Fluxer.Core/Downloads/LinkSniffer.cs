using System.Text.RegularExpressions;

namespace Fluxer.Core.Downloads;

/// Picks the links worth offering from copied text: magnets, and http(s) URLs whose path ends in a
/// file extension Fluxer has a category for. Web pages and bare domains are left alone, so copying a
/// link to share it does not pop a dialog.
public static partial class LinkSniffer
{
    [GeneratedRegex(@"(?:https?://[^\s""'<>]+|magnet:\?[^\s""'<>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    public static IReadOnlyList<string> DownloadLinks(string? text, AppSettings settings, int max = 50)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return [];
        var found = new List<string>();
        foreach (Match m in LinkPattern().Matches(text))
        {
            var link = m.Value.TrimEnd('.', ',', ';', ')', ']');
            if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) { if (link.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase)) found.Add(link); }
            else if (Uri.TryCreate(link, UriKind.Absolute, out var u) && LooksLikeFile(u, settings)) found.Add(link);
            if (found.Count >= max) break;
        }
        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    private static bool LooksLikeFile(Uri u, AppSettings settings)
    {
        var name = Uri.UnescapeDataString(u.AbsolutePath.TrimEnd('/'));
        var slash = name.LastIndexOf('/');
        name = slash >= 0 ? name[(slash + 1)..] : name;
        return name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || settings.CategoryFor(name) is not null;
    }
}

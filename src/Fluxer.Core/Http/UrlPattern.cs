using System.Globalization;
using System.Text.RegularExpressions;

namespace Fluxer.Core.Http;

/// Batch links from one pattern: "foto_[001-120].jpg" (zero padded like the first number), "[1-9]", "[a-z]",
/// "[0-100:5]" (step), several ranges multiply. A bracket that is not a range (IPv6 hosts, "a[]=1") stays as it is.
public static partial class UrlPattern
{
    public const int Max = 5000;

    [GeneratedRegex(@"\[(?:(\d{1,9})-(\d{1,9})(?::(\d{1,6}))?|([a-zA-Z])-([a-zA-Z]))\]")]
    private static partial Regex Range();

    public static bool Has(string s) => Ranges(s).Count > 0;

    /// The links the pattern stands for (null when it has no range). Count is checked first, so a typo like
    /// [1-99999999] costs nothing; `count` says how many it would make.
    public static IReadOnlyList<string>? Expand(string s, out long count, int max = Max)
    {
        var ranges = Ranges(s);
        count = 0;
        if (ranges.Count == 0) return null;
        count = ranges.Aggregate(1L, (n, r) => n > max ? n : n * r.Values.Count);
        if (count > max) return [];

        var result = new List<string> { "" };
        var at = 0;
        foreach (var r in ranges)
        {
            var literal = s[at..r.Start];
            result = [.. result.SelectMany(prefix => r.Values.Select(v => prefix + literal + v))];
            at = r.Start + r.Length;
        }
        var tail = s[at..];
        return [.. result.Select(p => p + tail)];
    }

    private sealed record Part(int Start, int Length, IReadOnlyList<string> Values);

    private static List<Part> Ranges(string s)
    {
        var list = new List<Part>();
        foreach (Match m in Range().Matches(s))
        {
            if (m.Groups[1].Success)
            {
                var (a, b) = (m.Groups[1].Value, m.Groups[2].Value);
                long from = long.Parse(a, CultureInfo.InvariantCulture), to = long.Parse(b, CultureInfo.InvariantCulture);
                var step = m.Groups[3].Success ? long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
                if (step <= 0) continue;
                var width = (a.Length > 1 && a[0] == '0') || (b.Length > 1 && b[0] == '0') ? Math.Max(a.Length, b.Length) : 0;
                var n = Math.Abs(to - from) / step + 1;
                if (n > Max) { list.Add(new Part(m.Index, m.Length, new Placeholder(n))); continue; }
                var dir = to >= from ? 1 : -1;
                list.Add(new Part(m.Index, m.Length,
                    [.. Enumerable.Range(0, (int)n).Select(i => (from + dir * i * step).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'))]));
            }
            else
            {
                char a = m.Groups[4].Value[0], b = m.Groups[5].Value[0];
                if (char.IsUpper(a) != char.IsUpper(b)) continue;
                var dir = b >= a ? 1 : -1;
                list.Add(new Part(m.Index, m.Length, [.. Enumerable.Range(0, Math.Abs(b - a) + 1).Select(i => ((char)(a + dir * i)).ToString())]));
            }
        }
        return list;
    }

    /// A range too large to list; only its count is ever read.
    private sealed class Placeholder(long count) : IReadOnlyList<string>
    {
        public int Count => (int)Math.Min(count, int.MaxValue);
        public string this[int index] => throw new NotSupportedException();
        public IEnumerator<string> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

using Fluxer.Core.Downloads;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

/// Each download's speed every 2 s (the host's tick) for the last hour, for the detail panel's 10-minute and
/// 1-hour speed graphs. A download that has not moved for an hour is forgotten.
public sealed class SpeedRecorder
{
    public const int Samples = 1800;
    private readonly Dictionary<long, (Queue<(double Down, double Up)> Q, int Idle)> _byId = [];

    public void Record(IEnumerable<IDownload> items)
    {
        var seen = new HashSet<long>();
        foreach (var d in items)
        {
            var moving = d.State is DownloadState.Downloading or DownloadState.Seeding;
            if (!moving && !_byId.ContainsKey(d.Id)) continue;
            seen.Add(d.Id);
            var (q, idle) = _byId.TryGetValue(d.Id, out var e) ? e : (new Queue<(double, double)>(), 0);
            q.Enqueue((d.State == DownloadState.Downloading ? d.GetProgress().BytesPerSecond : 0, d is TorrentDownload t ? t.UploadRate : 0));
            while (q.Count > Samples) q.Dequeue();
            _byId[d.Id] = (q, moving ? 0 : idle + 1);
        }
        foreach (var id in _byId.Where(p => !seen.Contains(p.Key) || p.Value.Idle >= Samples).Select(p => p.Key).ToList()) _byId.Remove(id);
    }

    /// The last `seconds` (2 s samples), averaged into points of `perPoint` samples each.
    public (double[] Down, double[] Up) Last(long id, int seconds, int perPoint)
    {
        if (!_byId.TryGetValue(id, out var e)) return ([], []);
        var samples = e.Q.TakeLast(seconds / 2).ToArray();
        var points = (samples.Length + perPoint - 1) / perPoint;
        double[] down = new double[points], up = new double[points];
        // Points are counted back from the newest sample, so the oldest one may average fewer samples.
        for (var p = 0; p < points; p++)
        {
            var end = samples.Length - (points - 1 - p) * perPoint;
            var start = Math.Max(0, end - perPoint);
            double sd = 0, su = 0;
            for (var i = start; i < end; i++) { sd += samples[i].Down; su += samples[i].Up; }
            (down[p], up[p]) = (sd / (end - start), su / (end - start));
        }
        return (down, up);
    }
}

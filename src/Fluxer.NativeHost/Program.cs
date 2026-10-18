using System.Diagnostics;
using Fluxer.Ipc;

// Native messaging host (Chromium browsers and Firefox): one message in on stdin, one reply out on stdout, then exit.
// It only validates and relays to the app's named pipe; it starts the app (tray only) if needed.

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var id = "";

try
{
    if (Protocol.AllowedCaller(args) is null)
        return await ReplyAsync(Envelope.Error(id, ErrorCodes.InvalidMessage, "unknown extension"));

    var body = await Framing.ReadAsync(stdin, Protocol.MaxRequestBytes, CancellationToken.None);
    if (body is null) return 0;

    var msg = Envelope.Parse(body);
    if (msg is null || string.IsNullOrEmpty(msg.Id)) return await ReplyAsync(Envelope.Error(id, ErrorCodes.InvalidMessage));
    id = msg.Id;
    if (msg.ProtocolVersion != Protocol.Version) return await ReplyAsync(Envelope.Error(id, ErrorCodes.ProtocolMismatch));
    if (Validate(msg) is { } problem) return await ReplyAsync(Envelope.Error(id, ErrorCodes.InvalidMessage, problem));

    // Status/ping must not launch the app (the popup polls them); real work does.
    var mayLaunch = msg.Type is MessageTypes.DownloadAdd or MessageTypes.PageSend or MessageTypes.PageResources or MessageTypes.DownloadBatch
        || msg.Type == MessageTypes.Command && msg.PayloadAs<Command>()?.Action == CommandActions.Show;
    var reply = await RelayAsync(body, mayLaunch);
    if (reply is null) return await ReplyAsync(Envelope.Error(id, ErrorCodes.AppUnavailable));
    if (reply.Length > Protocol.MaxReplyBytes) return await ReplyAsync(Envelope.Error(id, ErrorCodes.Internal, "reply too large"));
    await Framing.WriteAsync(stdout, reply, CancellationToken.None);
    return 0;
}
catch (Exception e)
{
    return await ReplyAsync(Envelope.Error(id, ErrorCodes.Internal, e.GetType().Name));
}

async Task<int> ReplyAsync(Envelope e)
{
    await Framing.WriteAsync(stdout, e.ToBytes(), CancellationToken.None);
    return 0;
}

static string? Validate(Envelope msg)
{
    switch (msg.Type)
    {
        case MessageTypes.Ping or MessageTypes.StatusGet:
            return null;
        case MessageTypes.DownloadAdd:
            var add = msg.PayloadAs<DownloadAdd>();
            if (add is null || !IsUrl(add.Url, allowMagnet: true)) return "bad url";
            // torrent-file: the .torrent the browser just saved; nothing else may be named here.
            return add.TorrentPath is null || (add.TorrentPath.Length < 1024 && Path.IsPathFullyQualified(add.TorrentPath)
                   && add.TorrentPath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) ? null : "bad path";
        case MessageTypes.PageSend:
            var page = msg.PayloadAs<PageSend>();
            return page is not null && IsUrl(page.PageUrl, false) ? null : "bad url";
        case MessageTypes.PageResources:
            var res = msg.PayloadAs<PageResources>();
            return res is not null && IsUrl(res.PageUrl, false) && res.Resources.All(r => IsUrl(r.Url, false)) ? null : "bad url";
        case MessageTypes.DownloadBatch:
            var batch = msg.PayloadAs<DownloadBatch>();
            return batch is not null && IsUrl(batch.PageUrl, false) && batch.Items is { Length: > 0 and <= 1000 }
                   && batch.Items.All(i => IsUrl(i.Url, false) && (i.FileName is null || i.FileName.Length < 512)) ? null : "bad batch";
        case MessageTypes.Command:
            var cmd = msg.PayloadAs<Command>();
            return cmd is not null && (CommandActions.Global.Contains(cmd.Action) || CommandActions.PerDownload.Contains(cmd.Action) && cmd.Id is > 0)
                ? null : "bad command";
        default:
            return "unknown type";
    }
}

static bool IsUrl(string? s, bool allowMagnet) =>
    s is not null && s.Length < 32 * 1024 &&
    (allowMagnet && s.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
     Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps));

static async Task<byte[]?> RelayAsync(byte[] body, bool mayLaunch)
{
    try { return await PipeClient.SendAsync(body, TimeSpan.FromMilliseconds(300), CancellationToken.None); }
    catch (TimeoutException) when (mayLaunch) { }
    catch (TimeoutException) { return null; }

    var app = Path.Combine(AppContext.BaseDirectory, "Fluxer.exe");
    if (!File.Exists(app)) return null;
    Process.Start(new ProcessStartInfo(app, "--background") { UseShellExecute = false })?.Dispose();

    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(10))
    {
        try { return await PipeClient.SendAsync(body, TimeSpan.FromMilliseconds(500), CancellationToken.None); }
        catch (TimeoutException) { }
    }
    return null;
}

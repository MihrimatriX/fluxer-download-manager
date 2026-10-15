using System.Diagnostics;
using Fluxer.Ipc;

// Chrome native messaging host: one message in on stdin, one reply out on stdout, then exit.
// It only validates and relays to the app's named pipe; it starts the app (tray only) if needed.

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var id = "";

try
{
    // Chrome passes the caller origin first: chrome-extension://<id>/
    var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal)) ?? "";
    var extensionId = origin["chrome-extension://".Length..].TrimEnd('/');
    if (!Protocol.AllowedExtensionIds.Contains(extensionId))
        return await ReplyAsync(Envelope.Error(id, ErrorCodes.InvalidMessage, "unknown extension"));

    var body = await Framing.ReadAsync(stdin, Protocol.MaxRequestBytes, CancellationToken.None);
    if (body is null) return 0;

    var msg = Envelope.Parse(body);
    if (msg is null || string.IsNullOrEmpty(msg.Id)) return await ReplyAsync(Envelope.Error(id, ErrorCodes.InvalidMessage));
    id = msg.Id;
    if (msg.ProtocolVersion != Protocol.Version) return await ReplyAsync(Envelope.Error(id, ErrorCodes.ProtocolMismatch));
    if (Validate(msg) is { } problem) return await ReplyAsync(Envelope.Error(id, ErrorCodes.InvalidMessage, problem));

    // Status/ping must not launch the app (the popup polls them); real work does.
    var mayLaunch = msg.Type is MessageTypes.DownloadAdd or MessageTypes.PageSend or MessageTypes.PageResources;
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

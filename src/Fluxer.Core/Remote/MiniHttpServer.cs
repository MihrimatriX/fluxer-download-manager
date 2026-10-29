using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace Fluxer.Core.Remote;

/// Just enough HTTP/1.1 for the remote API: one request per connection, bounded headers and body, a few seconds to
/// send them, a handful of connections at a time. Kestrel would add ~20 MB to a self-contained package for this.
public sealed class MiniHttpServer : IAsyncDisposable
{
    public sealed record Request(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] Body, IPAddress Remote)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    public sealed record Response(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
    {
        public static Response Text(int status, string contentType, string body, IReadOnlyDictionary<string, string>? headers = null) =>
            new(status, contentType, Encoding.UTF8.GetBytes(body), headers);
    }

    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxBodyBytes = 8 * 1024 * 1024;     // a .torrent sent as base64 to aria2.addTorrent
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly Func<Request, Task<Response>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(16);
    private Task? _accept;

    public MiniHttpServer(IPAddress address, int port, Func<Request, Task<Response>> handler)
    {
        _listener = new TcpListener(address, port);
        _handler = handler;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// Throws SocketException when the port is taken.
    public void Start()
    {
        _listener.Start();
        _accept = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) { Log.Debug("Remote accept: {Error}", e.Message); continue; }
            if (!await _slots.WaitAsync(0)) { client.Dispose(); continue; }     // busy: drop rather than queue
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                cts.CancelAfter(ReadTimeout);
                var stream = client.GetStream();
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                Response response;
                try
                {
                    var request = await ReadAsync(stream, remote, cts.Token);
                    response = request is null ? Response.Text(400, "text/plain", "Bad request") : await _handler(request);
                }
                catch (InvalidDataException e) { response = Response.Text(e.Message == "413" ? 413 : 400, "text/plain", "Bad request"); }
                await WriteAsync(stream, response, cts.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception e) { Log.Warning(e, "Remote request failed"); }
        finally { _slots.Release(); }
    }

    internal static async Task<Request?> ReadAsync(Stream stream, IPAddress remote, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        int end;
        while ((end = HeaderEnd(buffer, filled)) < 0)
        {
            if (filled == buffer.Length) throw new InvalidDataException("431");
            var n = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (n == 0) return null;
            filled += n;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (headers.ContainsKey("Transfer-Encoding")) return null;      // chunked bodies are not needed here
        var length = headers.TryGetValue("Content-Length", out var cl) ? (long.TryParse(cl, out var l) && l >= 0 ? l : -1) : 0;
        if (length < 0) return null;
        if (length > MaxBodyBytes) throw new InvalidDataException("413");

        var body = new byte[length];
        var already = Math.Min(filled - (end + 4), (int)length);
        Array.Copy(buffer, end + 4, body, 0, already);
        for (var got = already; got < length;)
        {
            var n = await stream.ReadAsync(body.AsMemory(got), ct);
            if (n == 0) return null;
            got += n;
        }
        var target = start[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q < 0 ? target : target[..q]);
        return new Request(start[0].ToUpperInvariant(), path, q < 0 ? "" : target[(q + 1)..], headers, body, remote);
    }

    private static int HeaderEnd(byte[] b, int count)
    {
        for (var i = 3; i < count; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i - 3;
        return -1;
    }

    private static async Task WriteAsync(Stream stream, Response r, CancellationToken ct)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n")
            .Append("Content-Type: ").Append(r.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(r.Body.Length).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n");
        foreach (var (k, v) in r.Headers ?? new Dictionary<string, string>()) head.Append(k).Append(": ").Append(v).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await stream.WriteAsync(r.Body, ct);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 413 => "Payload Too Large", 421 => "Misdirected Request", 429 => "Too Many Requests", _ => "Error",
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_accept is not null) await _accept;
        _stop.Dispose();
    }
}

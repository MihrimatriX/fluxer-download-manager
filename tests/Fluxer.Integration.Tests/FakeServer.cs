using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fluxer.Integration.Tests;

/// Range-capable file server whose behaviour each test tunes: range support, per-connection
/// speed, dropped connections, concurrency limit (429), ETag/content changes, expiring tokens.
public sealed class FakeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _concurrent;
    private int _dropBudget;

    public long Size { get; set; } = 4 * 1024 * 1024;
    public bool Ranges { get; set; } = true;
    /// No Content-Length → Kestrel uses chunked transfer encoding.
    public bool Chunked { get; set; }
    public int Version { get; set; } = 1;
    public string ETag => $"\"v{Version}\"";
    /// Bytes/second per response for a given range start; 0 = unlimited.
    public Func<long, long> RateFor { get; set; } = _ => 0;
    public int MaxConcurrent { get; set; }
    public long DropAfter { get; set; }
    public int DropResponses { set => _dropBudget = value; }
    public HashSet<string>? ValidTokens { get; set; }
    public string FileName { get; set; } = "file.bin";

    public int PeakConcurrent;
    public int Requests;
    public int Throttled;

    private FakeServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.Run(HandleAsync);
    }

    public string BaseUrl { get; private set; } = "";

    public static async Task<FakeServer> StartAsync()
    {
        var s = new FakeServer();
        await s._app.StartAsync();
        s.BaseUrl = s._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return s;
    }

    public string Url(string? token = null) => $"{BaseUrl}/dl/{FileName}" + (token is null ? "" : $"?token={token}");

    public static byte ByteAt(long i, int version) => (byte)((i * 31) ^ (i >> 11) ^ (version * 97));

    public byte[] ExpectedSha256()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 16];
        for (long off = 0; off < Size; off += buf.Length)
        {
            var n = (int)Math.Min(buf.Length, Size - off);
            for (var i = 0; i < n; i++) buf[i] = ByteAt(off + i, Version);
            sha.AppendData(buf, 0, n);
        }
        return sha.GetHashAndReset();
    }

    /// Extra static text responses by path (e.g. checksum files).
    public Dictionary<string, string> Texts { get; } = [];

    private async Task HandleAsync(HttpContext ctx)
    {
        if (Texts.TryGetValue(ctx.Request.Path.Value ?? "", out var text))
        {
            await ctx.Response.WriteAsync(text);
            return;
        }
        Interlocked.Increment(ref Requests);
        var c = Interlocked.Increment(ref _concurrent);
        int peak;
        while (c > (peak = Volatile.Read(ref PeakConcurrent)) && Interlocked.CompareExchange(ref PeakConcurrent, c, peak) != peak) { }
        try
        {
            if (ValidTokens is not null && !ValidTokens.Contains(ctx.Request.Query["token"].ToString()))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            if (MaxConcurrent > 0 && c > MaxConcurrent)
            {
                Interlocked.Increment(ref Throttled);
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers.RetryAfter = "0";
                return;
            }

            long from = 0, to = Size - 1;
            var partial = false;
            var ifRange = ctx.Request.Headers.IfRange.ToString();
            if (Ranges && RangeHeaderValue.TryParse(ctx.Request.Headers.Range.ToString(), out var range)
                && (ifRange.Length == 0 || ifRange == ETag))
            {
                var r = range.Ranges.First();
                from = r.From ?? Size - r.To!.Value;
                to = r.To is { } t && r.From is not null ? Math.Min(t, Size - 1) : Size - 1;
                partial = true;
                if (from >= Size) { ctx.Response.StatusCode = 416; return; }
            }

            var res = ctx.Response;
            res.StatusCode = partial ? 206 : 200;
            res.Headers.ETag = ETag;
            if (Ranges) res.Headers.AcceptRanges = "bytes";
            if (partial) res.Headers.ContentRange = $"bytes {from}-{to}/{Size}";
            res.Headers.ContentDisposition = $"attachment; filename=\"{FileName}\"";
            res.ContentType = "application/octet-stream";
            if (!Chunked) res.ContentLength = to - from + 1;

            var rate = RateFor(from);
            var drop = DropAfter > 0 && Interlocked.Decrement(ref _dropBudget) >= 0;
            var buf = new byte[16 * 1024];
            var started = DateTime.UtcNow;
            long sent = 0;
            for (var off = from; off <= to;)
            {
                var n = (int)Math.Min(buf.Length, to - off + 1);
                for (var i = 0; i < n; i++) buf[i] = ByteAt(off + i, Version);
                await res.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                off += n;
                sent += n;
                if (drop && sent >= DropAfter) { ctx.Abort(); return; }
                if (ValidTokens is { } valid && !valid.Contains(ctx.Request.Query["token"].ToString())) { ctx.Abort(); return; }
                if (rate > 0)
                {
                    var due = started + TimeSpan.FromSeconds(sent / (double)rate);
                    var wait = due - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ctx.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

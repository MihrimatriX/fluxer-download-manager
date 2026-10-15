using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluxer.Ipc;

public static class Protocol
{
    public const int Version = 1;
    public const string HostName = "com.fluxer.host";
    public const int MaxRequestBytes = 4 * 1024 * 1024;
    public const int MaxReplyBytes = 1024 * 1024;     // Chrome's host → extension limit

    /// Unpacked dev build (fixed "key" in manifest). Store IDs get added at publish time.
    public static readonly string[] AllowedExtensionIds = ["dckkomckipcaglacbgfmbgmapfckpkja"];

    /// Optional side-by-side profile (FLUXER_INSTANCE=dev): own pipe, mutex and data folder, no registry changes.
    public static string Instance { get; } = Environment.GetEnvironmentVariable("FLUXER_INSTANCE") ?? "";

    /// Per-user name; the pipe itself is also created with CurrentUserOnly.
    public static string PipeName => "Fluxer.v1." + WindowsIdentity.GetCurrent().User!.Value + (Instance.Length > 0 ? "." + Instance : "");
}

public sealed record Envelope(int ProtocolVersion, string Id, string Type, JsonElement Payload)
{
    public static Envelope Create<T>(string id, string type, T payload) =>
        new(Protocol.Version, id, type, JsonSerializer.SerializeToElement(payload, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)IpcJson.Default.GetTypeInfo(typeof(T))!));

    public T? PayloadAs<T>() =>
        Payload.Deserialize((System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)IpcJson.Default.GetTypeInfo(typeof(T))!);

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, IpcJson.Default.Envelope);

    public static Envelope? Parse(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize(json, IpcJson.Default.Envelope); }
        catch (JsonException) { return null; }
    }

    public static Envelope Error(string id, string code, string? message = null) =>
        Create(id, MessageTypes.Error, new ErrorReply(code, code == ErrorCodes.ProtocolMismatch ? Protocol.Version : null, message));
}

public static class MessageTypes
{
    public const string Ping = "ping", Pong = "pong";
    public const string DownloadAdd = "download.add", DownloadAddResult = "download.add.result";
    public const string StatusGet = "status.get", Status = "status";
    public const string PageSend = "page.send", PageResources = "page.resources";
    public const string InstanceArgs = "instance.args";
    public const string Error = "error";
}

public static class ErrorCodes
{
    public const string ProtocolMismatch = "protocol-mismatch", InvalidMessage = "invalid-message",
        AppUnavailable = "app-unavailable", Internal = "internal";
}

public sealed record CookieDto(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, double? ExpirationDate);

public sealed record DownloadAdd(
    string Url, string? FinalUrl, string? FileName, string? Mime, long? TotalBytes, string Source, string? TabUrl,
    string? Referrer, string? UserAgent, CookieDto[]? Cookies, bool Incognito, string? TorrentPath = null);

public sealed record PageSend(string PageUrl, string? Title, string? Referrer, string? UserAgent, CookieDto[]? Cookies, bool Incognito);

public sealed record PageResource(string Url, string Kind, int? Width, int? Height);

public sealed record PageResources(string PageUrl, PageResource[] Resources, string? Referrer, string? UserAgent, CookieDto[]? Cookies, bool Incognito);

public sealed record DownloadAddResult(string Status, string? Reason);
public sealed record StatusItem(string Name, double Progress, long BytesPerSecond);
public sealed record StatusReply(int Active, int Queued, long BytesPerSecond, StatusItem[] Top);
public sealed record ErrorReply(string Code, int? MinProtocolVersion, string? Message);
public sealed record Pong(string AppVersion);
public sealed record Empty;
public sealed record InstanceArgs(string[] Args);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(DownloadAdd))]
[JsonSerializable(typeof(PageSend))]
[JsonSerializable(typeof(PageResources))]
[JsonSerializable(typeof(DownloadAddResult))]
[JsonSerializable(typeof(StatusReply))]
[JsonSerializable(typeof(ErrorReply))]
[JsonSerializable(typeof(Pong))]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(InstanceArgs))]
public sealed partial class IpcJson : JsonSerializerContext;

/// uint32 little-endian length + UTF-8 JSON — Chrome's native messaging framing, reused for the pipe.
public static class Framing
{
    public static async Task<byte[]?> ReadAsync(Stream s, int maxBytes, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(s, header, ct)) return null;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > maxBytes) throw new InvalidDataException($"message too large: {len}");
        var body = new byte[len];
        return await ReadExactAsync(s, body, ct) ? body : null;
    }

    public static async Task WriteAsync(Stream s, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await s.WriteAsync(header, ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0) return false; // clean close between messages
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }
}

public static class PipeClient
{
    /// One request/response round trip. Throws TimeoutException if no app is listening.
    public static async Task<byte[]?> SendAsync(byte[] request, TimeSpan connectTimeout, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeout, ct);
        await Framing.WriteAsync(pipe, request, ct);
        return await Framing.ReadAsync(pipe, Protocol.MaxRequestBytes, ct);
    }
}

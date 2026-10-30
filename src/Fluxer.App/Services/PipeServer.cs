using System.IO.Pipes;
using Fluxer.Ipc;
using Serilog;

namespace Fluxer.App.Services;

/// Accepts one framed request per connection from the native host or a second app instance.
/// CurrentUserOnly gives the pipe an ACL for the current user only; no TCP port is opened.
public sealed class PipeServer(Func<Envelope, Task<Envelope>> handler) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        for (var i = 0; i < 2; i++) _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Protocol.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, ct), ct);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception e)
            {
                pipe?.Dispose();
                Log.Warning(e, "Pipe accept failed");
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        string id = "";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var body = await Framing.ReadAsync(pipe, Protocol.MaxRequestBytes, timeout.Token);
            if (body is null) return;
            var msg = Envelope.Parse(body);
            Envelope reply;
            if (msg is null) reply = Envelope.Error("", ErrorCodes.InvalidMessage);
            else if (msg.ProtocolVersion != Protocol.Version) reply = Envelope.Error(msg.Id, ErrorCodes.ProtocolMismatch);
            else
            {
                id = msg.Id;
                reply = await handler(msg);
            }
            await Framing.WriteAsync(pipe, reply.ToBytes(), timeout.Token);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Pipe request failed");
            try { await Framing.WriteAsync(pipe, Envelope.Error(id, ErrorCodes.Internal).ToBytes(), CancellationToken.None); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

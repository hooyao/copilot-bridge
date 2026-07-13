using System.IO.Pipes;
using System.Text;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// Named-pipe transport for the update control and readiness channels. The
/// updater is always the pipe SERVER (it owns the attempt); the parent bridge's
/// handoff side and each launched bridge's readiness side are CLIENTS. Pipes are
/// current-user scoped where the platform supports it, and every message is a
/// single length-bounded UTF-8 JSON line. All operations honor a bounded timeout
/// so no side can hang the transaction.
/// </summary>
internal static class UpdatePipeTransport
{
    /// <summary>
    /// Server-side: wait (bounded) for one client to connect and send one line.
    /// Returns the raw line, or null on timeout / disconnect / oversize.
    /// </summary>
    public static async Task<string?> ServerReceiveLineAsync(
        string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
            return await ReadLineAsync(server, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null; // timeout
        }
        catch (IOException)
        {
            return null; // disconnect
        }
    }

    /// <summary>
    /// Server-side: wait for a client, send <paramref name="line"/>, and (when
    /// <paramref name="expectReply"/>) read one reply line back. Returns the reply
    /// (or empty string when not expecting one), or null on failure.
    /// </summary>
    public static async Task<string?> ServerSendLineAsync(
        string pipeName, string line, bool expectReply, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
            await WriteLineAsync(server, line, cts.Token).ConfigureAwait(false);
            return expectReply
                ? await ReadLineAsync(server, cts.Token).ConfigureAwait(false)
                : string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Client-side: connect to <paramref name="pipeName"/> and send one line.
    /// Used by a launched bridge to report Ready. Best-effort — a failure to
    /// report is treated by the updater as "not ready" via its own timeout.
    /// </summary>
    public static async Task<bool> ClientSendLineAsync(
        string pipeName, string line, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            await WriteLineAsync(client, line, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Client-side handoff: connect to the updater's handoff pipe, read the one
    /// control line it sends (Prepared / PreflightFailed), and — if
    /// <paramref name="makeReply"/> returns a non-null line — send that reply
    /// (CutoverAuthorized) back on the same connection. Returns the received line,
    /// or null on timeout / disconnect / oversize. Used by the parent bridge's
    /// startup gate, which is the CLIENT here (the updater owns the server).
    /// </summary>
    public static async Task<string?> ClientExchangeAsync(
        string pipeName, Func<string, string?> makeReply, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            var received = await ReadLineAsync(client, cts.Token).ConfigureAwait(false);
            if (received is null)
            {
                return null;
            }
            var reply = makeReply(received);
            if (reply is not null)
            {
                await WriteLineAsync(client, reply, cts.Token).ConfigureAwait(false);
            }
            return received;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            return null;
        }
    }

    private static async Task WriteLineAsync(PipeStream pipe, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(PipeStream pipe, CancellationToken ct)
    {
        var buffer = new byte[UpdatePipeCodec.MaxBytes + 1];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(used, buffer.Length - used), ct)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break; // EOF
            }
            used += read;
            var span = buffer.AsSpan(0, used);
            var newline = span.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(span[..newline]);
            }
        }
        // No newline within the bound — reject as oversize/malformed.
        return null;
    }
}

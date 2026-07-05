using System.Buffers;
using System.IO.Pipelines;

namespace CopilotBridge.Cli.Endpoints;

/// <summary>
/// Reads an inbound HTTP request body from its <see cref="PipeReader"/> into a
/// single pooled buffer whose lifetime is owned by the returned
/// <see cref="PooledBody"/>. Uses the request pipe to accumulate the body (no
/// hand-written buffer growth — the pipe does the buffering), copies the assembled
/// <see cref="ReadOnlySequence{T}"/> into one contiguous rented array with a single
/// <see cref="ReadOnlySequence{T}.CopyTo(System.Span{byte})"/>, then advances the
/// reader to release the pipe's buffer immediately.
/// </summary>
/// <remarks>
/// Ownership is expressed by <see cref="IDisposable"/>: the caller wraps the read
/// in a <c>using</c>, consumes <see cref="PooledBody.Memory"/> synchronously
/// (deserialize + audit capture), and the rented buffer returns to
/// <see cref="ArrayPool{T}.Shared"/> on dispose. The pipe's own buffer is released
/// inside this method (via <see cref="PipeReader.AdvanceTo(System.SequencePosition)"/>)
/// as soon as the body is copied out, so nothing large is pinned for the pipeline.
/// </remarks>
internal static class InboundBody
{
    /// <summary>
    /// Read <paramref name="reader"/> to completion into a pooled buffer. The
    /// returned <see cref="PooledBody"/> owns the buffer; dispose it (via
    /// <c>using</c>) after the bytes have been consumed synchronously. Production
    /// callers pass <c>httpCtx.Request.BodyReader</c>.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> fired, or the pending read was cancelled.
    /// </exception>
    public static async ValueTask<PooledBody> ReadPooledAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (result.IsCanceled)
            {
                // CancelPendingRead was invoked. Leave the reader in a consistent
                // (advanced) state, then surface as cancellation.
                reader.AdvanceTo(buffer.Start, buffer.End);
                throw new OperationCanceledException(ct);
            }

            if (result.IsCompleted)
            {
                // The whole body is now buffered in `buffer`. Copy it into one
                // contiguous pooled array (a single memcpy — no growth loop), then
                // release the pipe's buffer immediately.
                var len = checked((int)buffer.Length);
                // Rent at least 1 so an empty body still yields a real (returnable)
                // buffer; Memory below trims to the true length.
                var buf = ArrayPool<byte>.Shared.Rent(Math.Max(len, 1));
                buffer.CopyTo(buf);
                reader.AdvanceTo(buffer.End);
                return new PooledBody(buf, len);
            }

            // Not complete: consumed nothing, examined everything — the pipe keeps
            // accumulating the rest while retaining what we have already seen, so the
            // final (IsCompleted) read returns the whole body in one sequence.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}

/// <summary>
/// Owns a rented buffer holding an inbound body. Exposes the meaningful prefix as
/// <see cref="Memory"/> (already trimmed to <see cref="Length"/> — the rented
/// capacity is never visible), and returns the buffer to the pool exactly once on
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class PooledBody : IDisposable
{
    private byte[]? _buffer;

    internal PooledBody(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    /// <summary>Content length in bytes (NOT the rented capacity).</summary>
    public int Length { get; }

    /// <summary>The body bytes, trimmed to <see cref="Length"/>. Valid until <see cref="Dispose"/>.</summary>
    public ReadOnlyMemory<byte> Memory =>
        new(_buffer ?? throw new ObjectDisposedException(nameof(PooledBody)), 0, Length);

    /// <summary>Return the buffer to the pool. Idempotent — a second call is a no-op.</summary>
    public void Dispose()
    {
        var buf = _buffer;
        if (buf is null) return;
        _buffer = null;
        ArrayPool<byte>.Shared.Return(buf, clearArray: false);
    }
}

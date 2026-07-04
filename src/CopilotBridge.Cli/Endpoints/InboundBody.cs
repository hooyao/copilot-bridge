using System.Buffers;

namespace CopilotBridge.Cli.Endpoints;

/// <summary>
/// Reads an inbound HTTP request body into a pooled buffer whose lifetime is owned
/// by the returned <see cref="PooledBody"/>. Replaces the hand-rolled
/// <c>ReadBodyPooledAsync</c> that was duplicated across endpoints and leaked a
/// (buffer, length) tuple whose rented capacity differed from the real length.
/// </summary>
/// <remarks>
/// Ownership is expressed by <see cref="IDisposable"/>: the caller wraps the read
/// in a <c>using</c>, consumes <see cref="PooledBody.Memory"/> synchronously
/// (deserialize + audit capture), and the buffer returns to
/// <see cref="ArrayPool{T}.Shared"/> on dispose. The body does NOT cross an
/// <c>await</c> into the pipeline — the endpoint uses it before running the
/// pipeline and disposes it, so the pooled buffer is not pinned for the whole
/// request.
/// </remarks>
internal static class InboundBody
{
    // Initial rent size. Matches the prior ClaudeCode/Codex endpoints (64 KiB);
    // grows by doubling for larger bodies. count_tokens kept its own smaller read.
    private const int InitialRent = 64 * 1024;

    /// <summary>
    /// Read <paramref name="body"/> to end into a pooled buffer. The returned
    /// <see cref="PooledBody"/> owns the buffer; dispose it (via <c>using</c>) after
    /// the bytes have been consumed synchronously.
    /// </summary>
    public static async Task<PooledBody> ReadPooledAsync(Stream body, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(InitialRent);
        var written = 0;
        try
        {
            while (true)
            {
                if (written == buf.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, bigger, 0, written);
                    ArrayPool<byte>.Shared.Return(buf, clearArray: false);
                    buf = bigger;
                }
                var n = await body.ReadAsync(buf.AsMemory(written), ct).ConfigureAwait(false);
                if (n == 0) break;
                written += n;
            }
        }
        catch
        {
            // Read failed mid-way: return the buffer before surfacing, so a faulted
            // read never leaks a rented array.
            ArrayPool<byte>.Shared.Return(buf, clearArray: false);
            throw;
        }
        return new PooledBody(buf, written);
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

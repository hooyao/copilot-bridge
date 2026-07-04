using System.Text;
using CopilotBridge.Cli.Endpoints;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract for the shared inbound-body reader. The type's job is to read a
/// stream to end into a POOLED buffer, expose ONLY the meaningful prefix (never
/// the rented capacity), and return the buffer to the pool exactly once. These
/// assert those invariants directly — they are the reason the endpoints can drop
/// their hand-rolled read loops and trust one helper.
/// </summary>
public class InboundBodyTests
{
    // A stream that hands back at most `chunk` bytes per read, to force the reader
    // through multiple ReadAsync calls (and, for large inputs, ≥1 buffer regrow).
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            if (_pos >= data.Length) return 0;
            var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>
    /// Contract: a body SMALLER than the initial rent round-trips byte-exact via
    /// Memory, and Length is the content length.
    /// </summary>
    [Fact]
    public async Task SmallBody_RoundTripsExact_LengthIsContentLength()
    {
        var data = Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-8","hi":"世界"}""");
        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, chunk: 7), default);

        Assert.Equal(data.Length, inbound.Length);
        Assert.Equal(data, inbound.Memory.ToArray());
    }

    /// <summary>
    /// Contract: a body LARGER than the 64 KiB initial rent (forces at least one
    /// doubling regrow) still round-trips byte-exact — the grow-and-copy must not
    /// drop or duplicate any bytes. This is the row that guards the regrow path.
    /// </summary>
    [Fact]
    public async Task LargeBody_ForcesRegrow_RoundTripsExact()
    {
        // 200 KiB of a non-repeating pattern so any mis-copied region is detectable.
        var data = new byte[200 * 1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 31 + 7) & 0xFF);

        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, chunk: 4096), default);

        Assert.Equal(data.Length, inbound.Length);
        Assert.True(inbound.Memory.Span.SequenceEqual(data), "regrown buffer must equal the input byte-for-byte");
    }

    /// <summary>
    /// Contract: Memory exposes the content length, NOT the (larger) rented
    /// capacity. An empty body yields a zero-length Memory, not a 64 KiB slice.
    /// </summary>
    [Fact]
    public async Task EmptyBody_YieldsZeroLengthMemory_NotRentedCapacity()
    {
        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream([], chunk: 8), default);

        Assert.Equal(0, inbound.Length);
        Assert.Equal(0, inbound.Memory.Length);
    }

    /// <summary>
    /// Contract: Dispose is idempotent — a double dispose does not throw and does
    /// not double-return to the pool (which would corrupt later rents). We can't
    /// directly observe a double-return, so assert the observable: no throw, and a
    /// subsequent independent read still works correctly.
    /// </summary>
    [Fact]
    public async Task DoubleDispose_NoThrow_PoolStaysSane()
    {
        var data = Encoding.UTF8.GetBytes("payload");
        var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, chunk: 3), default);
        inbound.Dispose();
        inbound.Dispose(); // must be a no-op, not a second Return

        // A fresh read after the double-dispose must still be correct (a
        // double-returned buffer could otherwise be handed out and corrupted).
        var data2 = Encoding.UTF8.GetBytes("second");
        using var inbound2 = await InboundBody.ReadPooledAsync(new ChunkedStream(data2, chunk: 2), default);
        Assert.Equal(data2, inbound2.Memory.ToArray());
    }

    /// <summary>
    /// Contract: reading Memory after Dispose throws ObjectDisposedException rather
    /// than handing back a returned-to-pool buffer (use-after-free guard).
    /// </summary>
    [Fact]
    public async Task MemoryAfterDispose_Throws()
    {
        var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(Encoding.UTF8.GetBytes("x"), 4), default);
        inbound.Dispose();
        Assert.Throws<ObjectDisposedException>(() => inbound.Memory);
    }
}

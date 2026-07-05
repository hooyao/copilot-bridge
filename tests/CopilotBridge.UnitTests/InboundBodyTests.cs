using System.Text;
using CopilotBridge.Cli.Endpoints;
using Microsoft.IO;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract for the shared inbound-body reader. It reads a request body
/// <see cref="Stream"/> to end into ONE contiguous pooled buffer (backed by a
/// <c>RecyclableMemoryStream</c>), exposes the bytes trimmed to the true length,
/// and returns the pooled storage exactly once on dispose. These assert those
/// invariants against a chunked stream (many small reads), empty bodies,
/// cancellation, a faulted read, and PooledBody ownership.
/// </summary>
/// <remarks>
/// The reader deliberately reads from <c>httpCtx.Request.Body</c> (a Stream), NOT
/// from <c>Request.BodyReader</c> (a PipeReader). A Stream consumes bytes as it
/// reads, so it never stalls on Kestrel's request-body backpressure. A PipeReader
/// full-read that only examines (never consumes) until IsCompleted deadlocks once
/// the body exceeds Kestrel's ~1 MB buffer threshold — see the OOM/stall warnings
/// in the System.IO.Pipelines docs. That failure mode is invisible to a unit test
/// (a MemoryStream-backed reader has no backpressure), so the guard is the API
/// choice itself: read the Stream, which cannot deadlock.
/// </remarks>
public class InboundBodyTests
{
    // A stream that hands back at most `chunk` bytes per read, to force the reader
    // through many ReadAsync calls (so the recyclable stream spans multiple pooled
    // chunks and GetBuffer() must assemble them into one contiguous buffer).
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
            ct.ThrowIfCancellationRequested();
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

    // ── round-trip correctness across read shapes ─────────────────────────────

    /// <summary>
    /// Contract: a small body round-trips byte-exact via Memory, and Length is the
    /// content length.
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
    /// Contract: a body delivered across MANY small reads (chunk far below the body
    /// size, so the recyclable stream spans multiple pooled chunks that GetBuffer()
    /// must assemble) still round-trips byte-exact — no dropped or duplicated bytes.
    /// </summary>
    [Theory]
    [InlineData(1)]     // one byte per read — maximal fragmentation
    [InlineData(7)]     // odd small chunk
    [InlineData(4096)]  // realistic small chunk
    public async Task MultiRead_MultiChunk_ReassemblesExact(int chunk)
    {
        // Non-repeating pattern so any mis-copied/dropped region is detectable.
        var data = new byte[200 * 1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 31 + 7) & 0xFF);

        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, chunk), default);

        Assert.Equal(data.Length, inbound.Length);
        Assert.True(inbound.Memory.Span.SequenceEqual(data),
            $"body reassembled from {chunk}-byte reads must equal the input byte-for-byte");
    }

    /// <summary>
    /// Contract: a multi-MB body (mirrors real Claude Code requests, which routinely
    /// exceed 4 MB) assembles correctly across many pooled chunks.
    /// </summary>
    [Fact]
    public async Task LargeBody_MultiMegabyte_ReassemblesExact()
    {
        var data = new byte[5 * 1024 * 1024]; // 5 MiB
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, chunk: 16 * 1024), default);

        Assert.Equal(data.Length, inbound.Length);
        Assert.True(inbound.Memory.Span.SequenceEqual(data));
    }

    /// <summary>
    /// Contract: Memory is trimmed to the content length, NOT the (larger) pooled
    /// chunk capacity. An empty body yields a zero-length Memory, not a full chunk.
    /// </summary>
    [Fact]
    public async Task EmptyBody_YieldsZeroLengthMemory_NotChunkCapacity()
    {
        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream([], chunk: 8), default);

        Assert.Equal(0, inbound.Length);
        Assert.Equal(0, inbound.Memory.Length);
    }

    // ── cancellation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Contract: a pre-cancelled token surfaces as OperationCanceledException (the read
    /// does not silently return a partial body). The no-leak half of the contract is
    /// asserted separately by <see cref="CancelledAfterPartialFill_LeaksNothing"/>.
    /// </summary>
    [Fact]
    public async Task CancelledToken_Throws()
    {
        var data = new byte[128 * 1024];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InboundBody.ReadPooledAsync(new ChunkedStream(data, chunk: 4096), cts.Token));
    }

    // A stream that yields a prefix then throws, to prove a faulted read disposes the
    // pooled stream (no leak) rather than swallowing.
    private sealed class FaultingStream(byte[] prefix) : Stream
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
            if (_pos >= prefix.Length) throw new IOException("simulated read fault");
            var n = Math.Min(buffer.Length, prefix.Length - _pos);
            prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
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
    /// Contract: a mid-read fault propagates (the helper does not swallow it). The
    /// no-leak half is asserted separately by <see cref="FaultedRead_LeaksNothing"/>.
    /// </summary>
    [Fact]
    public async Task FaultedRead_Propagates()
    {
        await Assert.ThrowsAsync<IOException>(async () =>
            await InboundBody.ReadPooledAsync(new FaultingStream(Encoding.UTF8.GetBytes("partial")), default));
    }

    // ── PooledBody ownership ──────────────────────────────────────────────────

    /// <summary>
    /// Contract: Dispose is idempotent — a double dispose does not throw and does
    /// not double-dispose the pooled stream (which would corrupt the pool). Asserted
    /// via the observable: no throw, and a subsequent independent read is still correct.
    /// </summary>
    [Fact]
    public async Task DoubleDispose_NoThrow_PoolStaysSane()
    {
        var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(Encoding.UTF8.GetBytes("payload"), 3), default);
        inbound.Dispose();
        inbound.Dispose(); // must be a no-op, not a second dispose of the stream

        var data2 = Encoding.UTF8.GetBytes("second-read-after-double-dispose");
        using var inbound2 = await InboundBody.ReadPooledAsync(new ChunkedStream(data2, 2), default);
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

    // ── leak accounting via an instrumented manager ───────────────────────────
    // A dedicated manager (not the process-wide one) lets us assert bytes are
    // returned to the pool. After every pooled stream is disposed, the manager's
    // in-use byte counters must be back to zero — i.e. nothing leaked.

    private static RecyclableMemoryStreamManager NewManager() => new();

    private static long InUse(RecyclableMemoryStreamManager m) =>
        m.SmallPoolInUseSize + m.LargePoolInUseSize;

    /// <summary>
    /// Contract: a SUCCESSFUL read that is then disposed returns all pooled storage —
    /// in-use bytes go back to zero. This is the baseline the leak tests below compare
    /// against, and it catches a `PooledBody.Dispose` that fails to dispose the stream.
    /// Mutation: drop `s.Dispose()` in PooledBody.Dispose → in-use stays > 0 → RED.
    /// </summary>
    [Fact]
    public async Task SuccessfulRead_Disposed_ReturnsAllPooledBytes()
    {
        var mgr = NewManager();
        // 200 KiB forces a large-pool assembly on GetBuffer(), so both pools are exercised.
        var data = new byte[200 * 1024];
        var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, 4096), mgr, default);
        _ = inbound.Memory; // force GetBuffer() (assembles the large buffer)
        Assert.True(InUse(mgr) > 0, "a live pooled read must hold pooled bytes");

        inbound.Dispose();

        Assert.Equal(0, InUse(mgr)); // everything returned — no leak
    }

    /// <summary>
    /// Contract: a FAULTED read leaks nothing — the read's catch disposes the pooled
    /// stream before rethrowing, so in-use bytes return to zero even though no
    /// PooledBody was ever handed back. Mutation: delete `ms.Dispose()` from the catch
    /// in ReadPooledAsync → in-use stays > 0 after the throw → RED. (The old "it throws"
    /// assertion could not see this; this one can.)
    /// </summary>
    [Fact]
    public async Task FaultedRead_LeaksNothing()
    {
        var mgr = NewManager();
        await Assert.ThrowsAsync<IOException>(async () =>
            await InboundBody.ReadPooledAsync(new FaultingStream(Encoding.UTF8.GetBytes("partial")), mgr, default));

        Assert.Equal(0, InUse(mgr)); // faulted read returned its pooled blocks
    }

    /// <summary>
    /// Contract: a read cancelled AFTER partially filling the stream also leaks
    /// nothing — the catch disposes a partially-written stream. This is the realistic
    /// client-disconnect shape (bytes already buffered, then abort), distinct from
    /// pre-cancellation. Mutation: delete the catch `ms.Dispose()` → in-use > 0 → RED.
    /// </summary>
    [Fact]
    public async Task CancelledAfterPartialFill_LeaksNothing()
    {
        var mgr = NewManager();
        using var cts = new CancellationTokenSource();
        // Stream yields a prefix, then cancels the token, then the next read observes it.
        var stream = new CancelAfterPrefixStream(new byte[64 * 1024], cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InboundBody.ReadPooledAsync(stream, mgr, cts.Token));

        Assert.Equal(0, InUse(mgr));
    }

    // A stream that delivers `prefix` across reads, cancels `cts` once the prefix is
    // exhausted, so the following read throws OCE with data already in the RMS.
    private sealed class CancelAfterPrefixStream(byte[] prefix, CancellationTokenSource cts) : Stream
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
            ct.ThrowIfCancellationRequested();
            if (_pos >= prefix.Length)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return 0;
            }
            var n = Math.Min(buffer.Length, prefix.Length - _pos);
            prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
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

    // ── single/multi-block boundary (RMS default BlockSize = 128 KiB) ──────────

    /// <summary>
    /// Contract: bodies straddling the RMS block-size boundary (128 KiB) round-trip
    /// byte-exact — the single-block `GetBuffer()` return path (≤ 1 block) and the
    /// multi-block assembly path (&gt; 1 block) must agree at the seam. Catches an
    /// off-by-one in the single-vs-multi branch or a dropped final-block boundary byte.
    /// </summary>
    [Theory]
    [InlineData(128 * 1024 - 1)]  // one below a full block
    [InlineData(128 * 1024)]      // exactly one block
    [InlineData(128 * 1024 + 1)]  // one above — flips to multi-block assembly
    public async Task BlockSizeBoundary_RoundTripsExact(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)((i * 131 + 17) & 0xFF);

        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, chunk: 9999), default);

        Assert.Equal(size, inbound.Length);
        Assert.True(inbound.Memory.Span.SequenceEqual(data),
            $"body of {size} bytes (block boundary) must round-trip byte-for-byte");
    }

    /// <summary>
    /// Contract: reading Memory twice returns the SAME assembled view (the `_view`
    /// cache), not a re-assembled/corrupted copy. Mutation: replace `_view ??=` with an
    /// unconditional re-assembly and this still passes on content — but asserting the
    /// two reads are byte-identical AND that a large (multi-block) body is stable pins
    /// the caching contract's observable result.
    /// </summary>
    [Fact]
    public async Task MemoryReadTwice_IsStable()
    {
        var data = new byte[200 * 1024]; // multi-block, so GetBuffer() assembles
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

        using var inbound = await InboundBody.ReadPooledAsync(new ChunkedStream(data, 4096), default);

        var first = inbound.Memory.ToArray();
        var second = inbound.Memory.ToArray();
        Assert.Equal(first, second);
        Assert.True(inbound.Memory.Span.SequenceEqual(data));
    }
}

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using CopilotBridge.Cli.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract for the shared inbound-body reader. It reads a request body from a
/// <see cref="PipeReader"/> into ONE pooled contiguous buffer, exposes only the
/// meaningful prefix (never the rented capacity), and returns the buffer to the
/// pool exactly once. These assert those invariants against realistic pipe
/// behaviour — multi-segment delivery, empty bodies, large bodies, cancellation,
/// and the real ASP.NET request pipe — the reasons the endpoints can trust one
/// helper instead of a hand-rolled read loop.
/// </summary>
public class InboundBodyTests
{
    // A real PipeReader over the bytes, delivered in `bufferSize` chunks. This is
    // the SAME adapter production uses (DefaultHttpContext.Request.BodyReader wraps
    // the body Stream in a StreamPipeReader), so a small bufferSize forces genuine
    // multi-segment ReadOnlySequences through the accumulation loop.
    private static PipeReader Reader(byte[] data, int bufferSize) =>
        PipeReader.Create(new MemoryStream(data),
            new StreamPipeReaderOptions(bufferSize: bufferSize, minimumReadSize: 1));

    // ── round-trip correctness across delivery shapes ─────────────────────────

    /// <summary>
    /// Contract: a body delivered in one read round-trips byte-exact via Memory,
    /// and Length is the content length.
    /// </summary>
    [Fact]
    public async Task SingleRead_RoundTripsExact_LengthIsContentLength()
    {
        var data = Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-8","hi":"世界"}""");
        using var inbound = await InboundBody.ReadPooledAsync(Reader(data, bufferSize: 64 * 1024), default);

        Assert.Equal(data.Length, inbound.Length);
        Assert.Equal(data, inbound.Memory.ToArray());
    }

    /// <summary>
    /// Contract: a body delivered across MANY small segments (bufferSize far below
    /// the body size) still assembles byte-exact. This is the core guard for the
    /// PipeReader accumulation loop: if AdvanceTo(consumed, examined) mishandled the
    /// "keep everything until complete" contract, the reassembly would corrupt.
    /// </summary>
    [Theory]
    [InlineData(1)]     // one byte per read — maximal fragmentation
    [InlineData(7)]     // odd small chunk
    [InlineData(4096)]  // realistic small buffer
    public async Task MultiSegment_ReassemblesExact(int bufferSize)
    {
        // Non-repeating pattern so any mis-ordered/dropped segment is detectable.
        var data = new byte[200 * 1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 31 + 7) & 0xFF);

        using var inbound = await InboundBody.ReadPooledAsync(Reader(data, bufferSize), default);

        Assert.Equal(data.Length, inbound.Length);
        Assert.True(inbound.Memory.Span.SequenceEqual(data),
            $"body reassembled from {bufferSize}-byte segments must equal the input byte-for-byte");
    }

    /// <summary>
    /// Contract: a large multi-MB body (mirrors real Claude Code requests) assembles
    /// correctly. Guards that `checked((int)buffer.Length)` and the single CopyTo
    /// handle a body well beyond the pooled initial rent.
    /// </summary>
    [Fact]
    public async Task LargeBody_MultiMegabyte_ReassemblesExact()
    {
        var data = new byte[5 * 1024 * 1024]; // 5 MiB
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

        using var inbound = await InboundBody.ReadPooledAsync(Reader(data, bufferSize: 16 * 1024), default);

        Assert.Equal(data.Length, inbound.Length);
        Assert.True(inbound.Memory.Span.SequenceEqual(data));
    }

    /// <summary>
    /// Contract: Memory exposes the content length, NOT the rented capacity. An
    /// empty body yields a zero-length Memory, and disposes cleanly (a real,
    /// returnable buffer was still rented).
    /// </summary>
    [Fact]
    public async Task EmptyBody_YieldsZeroLengthMemory()
    {
        using var inbound = await InboundBody.ReadPooledAsync(Reader([], bufferSize: 8), default);

        Assert.Equal(0, inbound.Length);
        Assert.Equal(0, inbound.Memory.Length);
    }

    // ── integration with the real ASP.NET request pipe ────────────────────────

    /// <summary>
    /// Contract: reading via a real <see cref="HttpRequest.BodyReader"/> (the
    /// production source) round-trips the body a test assigns to
    /// <see cref="HttpRequest.Body"/>. This both proves the production path and
    /// validates that the endpoint tests — which set <c>Request.Body</c> — exercise
    /// the same bytes through <c>BodyReader</c>.
    /// </summary>
    [Fact]
    public async Task RealRequestBodyReader_RoundTrips()
    {
        var data = Encoding.UTF8.GetBytes("""{"model":"gpt-5.3-codex","probe":"body-reader"}""");
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(data);

        using var inbound = await InboundBody.ReadPooledAsync(http.Request.BodyReader, default);

        Assert.Equal(data, inbound.Memory.ToArray());
    }

    // ── cancellation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Contract: a cancelled token surfaces as OperationCanceledException (the read
    /// does not silently return a partial body).
    /// </summary>
    [Fact]
    public async Task CancelledToken_Throws()
    {
        var data = new byte[128 * 1024];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InboundBody.ReadPooledAsync(Reader(data, bufferSize: 4096), cts.Token));
    }

    // A PipeReader whose first ReadAsync reports IsCanceled (as CancelPendingRead
    // would), to exercise the IsCanceled branch deterministically.
    private sealed class CanceledReader : PipeReader
    {
        public bool Advanced { get; private set; }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken ct = default) =>
            new(new ReadResult(new ReadOnlySequence<byte>(new byte[3]), isCanceled: true, isCompleted: false));
        public override void AdvanceTo(SequencePosition consumed) => Advanced = true;
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => Advanced = true;
        public override bool TryRead(out ReadResult result) { result = default; return false; }
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }
    }

    /// <summary>
    /// Contract: a pending-read cancellation (ReadResult.IsCanceled) surfaces as
    /// OperationCanceledException AND leaves the reader advanced (consistent state),
    /// rather than looping forever or leaking.
    /// </summary>
    [Fact]
    public async Task PendingReadCanceled_Throws_AndAdvancesReader()
    {
        var reader = new CanceledReader();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InboundBody.ReadPooledAsync(reader, default));

        Assert.True(reader.Advanced, "IsCanceled path must AdvanceTo to keep the reader consistent");
    }

    // A PipeReader whose ReadAsync throws — to prove a faulted read leaks no rental.
    private sealed class FaultingReader : PipeReader
    {
        public override ValueTask<ReadResult> ReadAsync(CancellationToken ct = default) =>
            throw new IOException("simulated pipe fault");
        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override bool TryRead(out ReadResult result) { result = default; return false; }
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }
    }

    /// <summary>
    /// Contract: a fault before completion propagates and rents nothing to leak.
    /// (The helper only rents on IsCompleted, so a pre-completion throw cannot leave
    /// a rented array un-returned — this asserts the exception surfaces cleanly.)
    /// </summary>
    [Fact]
    public async Task FaultedRead_Propagates()
    {
        await Assert.ThrowsAsync<IOException>(async () =>
            await InboundBody.ReadPooledAsync(new FaultingReader(), default));
    }

    // ── PooledBody ownership ──────────────────────────────────────────────────

    /// <summary>
    /// Contract: Dispose is idempotent — a double dispose does not throw and does
    /// not double-return to the pool (which would corrupt later rents). Asserted via
    /// the observable: no throw, and a subsequent independent read is still correct.
    /// </summary>
    [Fact]
    public async Task DoubleDispose_NoThrow_PoolStaysSane()
    {
        var inbound = await InboundBody.ReadPooledAsync(Reader(Encoding.UTF8.GetBytes("payload"), 3), default);
        inbound.Dispose();
        inbound.Dispose(); // must be a no-op, not a second Return

        var data2 = Encoding.UTF8.GetBytes("second-read-after-double-dispose");
        using var inbound2 = await InboundBody.ReadPooledAsync(Reader(data2, 4), default);
        Assert.Equal(data2, inbound2.Memory.ToArray());
    }

    /// <summary>
    /// Contract: reading Memory after Dispose throws ObjectDisposedException rather
    /// than handing back a returned-to-pool buffer (use-after-free guard).
    /// </summary>
    [Fact]
    public async Task MemoryAfterDispose_Throws()
    {
        var inbound = await InboundBody.ReadPooledAsync(Reader(Encoding.UTF8.GetBytes("x"), 4), default);
        inbound.Dispose();
        Assert.Throws<ObjectDisposedException>(() => inbound.Memory);
    }
}

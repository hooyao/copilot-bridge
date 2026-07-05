using Microsoft.IO;

namespace CopilotBridge.Cli.Endpoints;

/// <summary>
/// Reads an inbound HTTP request body <see cref="Stream"/> into a pooled buffer
/// whose lifetime is owned by the returned <see cref="PooledBody"/>. Backed by
/// <see cref="RecyclableMemoryStream"/> (pooled chunks) so conversation-sized
/// bodies (4 MB+ for Claude Code) avoid per-request LOH allocation churn, and there
/// is no hand-rolled buffer growth — the recyclable stream grows itself as bytes
/// are copied in.
/// </summary>
/// <remarks>
/// Ownership is expressed by <see cref="IDisposable"/>: the caller consumes
/// <see cref="PooledBody.Memory"/> synchronously (deserialize + audit capture) and
/// then disposes the <see cref="PooledBody"/> — the endpoints dispose it right after
/// building the IR, so the pooled buffer (a large-pool buffer for a 4 MB+ body) is
/// returned to the manager in the brief parse window and NOT pinned across the
/// pipeline + streaming relay. The body never crosses an <c>await</c> into the
/// pipeline (the pipeline operates on the IR, not the raw bytes).
/// <para>
/// <b>Reads the body <see cref="Stream"/>, NOT <c>Request.BodyReader</c>.</b> A
/// Stream consumes bytes as it reads, so it never stalls on Kestrel's request-body
/// backpressure. A PipeReader full-read that examines-without-consuming until
/// <c>IsCompleted</c> deadlocks once the body exceeds Kestrel's ~1 MB buffer
/// threshold — do not switch this to a PipeReader.
/// </para>
/// </remarks>
internal static class InboundBody
{
    // Process-wide manager: thread-safe, holds the small/large chunk pools, lives
    // for the whole process (the library's documented pattern). Free-list caps are
    // set EXPLICITLY: the default (0) means "unbounded free list that never shrinks",
    // so a burst of concurrent 4 MB bodies would leave a permanent high-watermark of
    // retained large buffers. Cap the retained free bytes so idle memory returns to
    // the OS; buffers over the cap are dropped to GC on return rather than pooled.
    private static readonly RecyclableMemoryStreamManager Manager = new(
        new RecyclableMemoryStreamManager.Options
        {
            // Defaults: 128 KiB blocks, 1 MiB large-buffer multiple. Retain a bounded
            // working set of each pool; excess is released rather than hoarded.
            MaximumSmallPoolFreeBytes = 16L * 1024 * 1024,   // 16 MiB of 128 KiB blocks
            MaximumLargePoolFreeBytes = 64L * 1024 * 1024,   // 64 MiB of assembled buffers
        });

    /// <summary>
    /// Read <paramref name="body"/> to end into a pooled buffer. The returned
    /// <see cref="PooledBody"/> owns the buffer; dispose it (via <c>using</c>) after
    /// the bytes have been consumed synchronously.
    /// </summary>
    public static Task<PooledBody> ReadPooledAsync(Stream body, CancellationToken ct) =>
        ReadPooledAsync(body, Manager, ct);

    /// <summary>
    /// Test seam: read into a caller-supplied manager so a test can pass an
    /// instrumented <see cref="RecyclableMemoryStreamManager"/> and assert
    /// created-vs-disposed stream counts (e.g. that a faulted read leaks nothing).
    /// Production callers use the <see cref="ReadPooledAsync(Stream, CancellationToken)"/>
    /// overload, which supplies the process-wide manager.
    /// </summary>
    internal static async Task<PooledBody> ReadPooledAsync(
        Stream body, RecyclableMemoryStreamManager manager, CancellationToken ct)
    {
        var ms = manager.GetStream("InboundBody");
        try
        {
            await body.CopyToAsync(ms, ct).ConfigureAwait(false);
            // Compute the length INSIDE the try: the checked cast throws on a body
            // larger than int.MaxValue (~2 GB), and constructing PooledBody must not
            // happen outside the try or such a fault would orphan `ms` (leaking its
            // pooled blocks). A >2 GB body is only reachable if Kestrel's
            // MaxRequestBodySize is raised, but the guarantee holds regardless.
            var len = checked((int)ms.Length);
            return new PooledBody(ms, len);
        }
        catch
        {
            // Copy (or the length cast) failed: dispose so the pooled chunks return
            // before the exception surfaces — a faulted read never leaks buffers.
            ms.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Owns a <see cref="RecyclableMemoryStream"/> holding an inbound body. Exposes the
/// bytes as a contiguous <see cref="Memory"/> (trimmed to the true length — the
/// pooled capacity is never visible) and returns the pooled chunks exactly once on
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class PooledBody : IDisposable
{
    private RecyclableMemoryStream? _stream;
    // GetBuffer() assembles the chunked stream into a single (large-pool-backed)
    // buffer on first call and caches it; we cache the resulting view so repeated
    // Memory reads don't re-assemble. Null until first Memory access.
    private ReadOnlyMemory<byte>? _view;

    internal PooledBody(RecyclableMemoryStream stream, int length)
    {
        _stream = stream;
        Length = length;
    }

    /// <summary>Content length in bytes.</summary>
    public int Length { get; }

    /// <summary>
    /// The body bytes as one contiguous buffer, trimmed to <see cref="Length"/>.
    /// Valid until <see cref="Dispose"/>. Backed by the recyclable stream's pooled
    /// storage (a single large-pool block when the body spanned multiple chunks).
    /// Pooling avoids per-request LOH allocation churn (the buffers are reused); a
    /// multi-MB assembled buffer still physically lives on the LOH, it just isn't
    /// re-allocated per request.
    /// </summary>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            var s = _stream ?? throw new ObjectDisposedException(nameof(PooledBody));
            // GetBuffer() returns the single chunk directly when the body fit in one
            // block, else assembles+caches one large-pool buffer. Trim to Length.
            return _view ??= s.GetBuffer().AsMemory(0, Length);
        }
    }

    /// <summary>Return the pooled chunks to the manager. Idempotent — a second call is a no-op.</summary>
    public void Dispose()
    {
        var s = _stream;
        if (s is null) return;
        _stream = null;
        _view = null;
        s.Dispose();
    }
}

using System.Buffers;

namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Side buffer that accumulates the RAW upstream response bytes while the SSE
/// stream is consumed downstream. Exists so the <c>upstream-resp</c> audit can
/// record exactly what Copilot put on the wire, BEFORE any response stage (e.g.
/// <c>ResponseModelRewriteStage</c>) rewrites the body — see
/// <c>docs/pipeline-design.md</c> on the four-artifact trace contract.
/// </summary>
/// <remarks>
/// Threading: written by the single relay-loop consumer as it enumerates the
/// stream, then read exactly once by the endpoint's <c>finally</c> AFTER
/// enumeration completes. The two phases are strictly ordered by <c>await</c>
/// (the relay loop is awaited before the finally runs), so no locking is needed.
/// Only allocated when tracing is enabled; on the hot path it is never created.
/// </remarks>
internal sealed class RawResponseCapture
{
    private readonly ArrayBufferWriter<byte> _buf = new();
    private bool _sealed;

    /// <summary>
    /// Append raw bytes. Must NOT be called after <see cref="ToArray"/> has
    /// finalized the capture: the relay loop writes, then the endpoint reads
    /// exactly once. A post-finalize write is a wiring bug, so it throws rather
    /// than silently producing a capture that disagrees with what was read.
    /// (<see cref="TeeReadStream"/> guards this call, so such a throw can never
    /// reach — and truncate — the client's stream.)
    /// </summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_sealed)
        {
            throw new InvalidOperationException(
                "RawResponseCapture written after ToArray() finalized it.");
        }
        _buf.Write(bytes);
    }

    public int Length => _buf.WrittenCount;

    /// <summary>
    /// Snapshot the captured bytes into a fresh array and seal the buffer against
    /// further writes. The copy decouples the audit payload's lifetime from this
    /// buffer; sealing turns the documented "write phase then read phase"
    /// ordering into an enforced one.
    /// </summary>
    public byte[] ToArray()
    {
        _sealed = true;
        return _buf.WrittenSpan.ToArray();
    }
}

/// <summary>
/// Transparent read-through tee: every byte read from <paramref name="inner"/>
/// is also copied into a <see cref="RawResponseCapture"/>. Inserted between
/// <c>HttpContent.ReadAsStreamAsync()</c> and <c>SseParser.Create(...)</c> so
/// the parser drives the reads while we observe the raw bytes — the parsed
/// <c>SseItem</c>s downstream are byte-for-byte the same as without the tee.
/// </summary>
/// <remarks>
/// <para><b>Read-only / observe-only.</b> Write and seek throw; this never
/// mutates the stream, so the bytes forwarded to the client stay identical.
/// A capture-side failure is swallowed (see <see cref="Capture"/>) so the trace
/// can never perturb the real response — observability, not the response
/// contract.</para>
/// <para><b>Does NOT own the inner stream.</b> No <c>Dispose</c> override: the
/// SSE iterator's own <c>finally</c> disposes the raw network stream, and
/// double-disposing it would be a bug. This wrapper holds no disposable state of
/// its own, and the base <see cref="Stream.Dispose(bool)"/> never touches
/// <c>_inner</c> — so the non-ownership guarantee rests on the absence of any
/// dispose logic here. Do not add one.</para>
/// </remarks>
internal sealed class TeeReadStream : Stream
{
    private readonly Stream _inner;
    private readonly RawResponseCapture _capture;
    // Latches once a capture write fails (e.g. ArrayBufferWriter OOM / 2 GB cap)
    // so we stop attempting it — a failed capture must perturb the read path at
    // most once, never repeatedly.
    private bool _captureFailed;

    public TeeReadStream(Stream inner, RawResponseCapture capture)
    {
        _inner = inner;
        _capture = capture;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (n > 0) Capture(buffer.Span.Slice(0, n));
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        if (n > 0) Capture(buffer.Slice(0, n));
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Copy the just-read bytes into the capture, best-effort. A capture failure
    /// (OOM growing the buffer, the writer's hard size cap) must NEVER propagate
    /// into the read path and truncate the client's stream — the bytes have
    /// already been pulled off the socket into the caller's buffer, so the read
    /// itself succeeded. On failure we keep whatever was captured so far and
    /// stop capturing for the rest of the stream.
    /// </summary>
    private void Capture(ReadOnlySpan<byte> bytes)
    {
        if (_captureFailed) return;
        try
        {
            _capture.Write(bytes);
        }
        catch
        {
            _captureFailed = true;
        }
    }
}

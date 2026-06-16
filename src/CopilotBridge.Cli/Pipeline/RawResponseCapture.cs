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

    public void Write(ReadOnlySpan<byte> bytes) => _buf.Write(bytes);

    public int Length => _buf.WrittenCount;

    public byte[] ToArray() => _buf.WrittenSpan.ToArray();
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
/// mutates the stream, so the bytes forwarded to Claude Code stay identical.</para>
/// <para><b>Does NOT own the inner stream.</b> <see cref="Dispose(bool)"/>
/// deliberately never touches <c>_inner</c>: the SSE iterator's own
/// <c>finally</c> disposes the raw network stream, and double-disposing it
/// would be a bug. This wrapper holds no disposable state of its own.</para>
/// </remarks>
internal sealed class TeeReadStream : Stream
{
    private readonly Stream _inner;
    private readonly RawResponseCapture _capture;

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
        if (n > 0) _capture.Write(buffer.Span.Slice(0, n));
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        if (n > 0) _capture.Write(buffer.Slice(0, n));
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

    // Intentionally does NOT dispose _inner — the SSE iterator owns it.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

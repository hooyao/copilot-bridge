using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// The single per-request seam for "are we tracing this request?". Owns the one
/// <see cref="Enabled"/> flag (snapshotted from <see cref="TracingOptions"/>), the
/// four audit emissions, and the two trace-only buffer factories. Every method is
/// a cheap no-op when <see cref="Enabled"/> is false — no <c>BridgeIoPayload</c> is
/// built, no buffer is allocated, and any body copy needed only for the audit is
/// skipped. On-trace the <c>Record*</c> methods delegate verbatim to
/// <see cref="BridgeIoLoggerExtensions"/>, so the emitted artifacts are
/// byte-identical to calling those extensions directly.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>scoped DI service</b> (one per request scope), the same lifetime as
/// <see cref="BridgeContext{TBody}"/>; endpoints and strategies inject the same
/// instance. It replaces the five scattered gating shapes the refactor removed: the
/// two strategy <c>_tracingEnabled</c> fields, the three endpoint <c>tracingEnabled</c>
/// locals, the <c>?:</c> capture ternaries, and — the bug surface — the two
/// <b>unguarded</b> sites that re-serialized / stashed unconditionally. The DI-null
/// <see cref="BridgeIoSink"/> registration stays: that is the sink's own concern one
/// layer lower, and is unrelated to per-request gating.
/// </para>
/// <para>
/// <b>Zero-overhead contract.</b> When <see cref="Enabled"/> is false the request
/// path does no audit-only work. The <c>Record*</c> methods that take a
/// <see cref="ReadOnlyMemory{Byte}"/> body copy it to a <c>byte[]</c> ONLY when
/// enabled, so the endpoints' former unconditional <c>.ToArray()</c> no longer runs
/// off-trace. Callers must still avoid computing an expensive argument eagerly at the
/// call site (e.g. a serialize) — the strategies gate their <c>UpstreamWireBody</c>
/// stash on <see cref="Enabled"/> for exactly this reason.
/// </para>
/// </remarks>
internal sealed class RequestAudit
{
    private readonly ILogger _io;

    public RequestAudit(IOptions<TracingOptions> tracing, ILogger<MessagesRequest> io)
    {
        Enabled = tracing.Value.Enabled;
        _io = io;
    }

    /// <summary>True iff per-request tracing is on. The one place the flag is read
    /// on the request path; strategies gate their wire-body stash on it.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// A fresh raw-response capture when tracing is on, else null. Streaming
    /// strategies tee the upstream stream into it; a null return means "no tee, no
    /// allocation, byte-identical passthrough".
    /// </summary>
    public RawResponseCapture? NewCapture() => Enabled ? new RawResponseCapture() : null;

    /// <summary>
    /// A fresh per-event capture list when tracing is on, else null. The endpoint
    /// appends each outbound SSE event to it for the <c>inbound-resp</c> audit; null
    /// means the (potentially large) list is never grown off-trace.
    /// </summary>
    public List<CapturedSseEvent>? NewEventList() => Enabled ? new List<CapturedSseEvent>() : null;

    /// <summary>
    /// Emit the <c>inbound-req</c> artifact. No-op when off. Takes the inbound bytes
    /// as a view and copies to the audit array only when enabled, so the endpoints'
    /// former unconditional copy is now gated here.
    /// </summary>
    public void RecordInbound(
        int seq, string traceId, string method, string path,
        IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> body)
    {
        if (!Enabled) return;
        var arr = body.ToArray();
        _io.LogInboundRequest(seq, traceId, method, path, headers, arr, arr.Length);
    }

    /// <summary>
    /// Emit the <c>inbound-req</c> artifact from a caller-owned array — no copy.
    /// Use this when the caller already holds an independent (non-pooled) array it
    /// does not mutate after the call (e.g. count_tokens, which materializes the
    /// body once for its summary probe). The sink owns the array thereafter.
    /// </summary>
    public void RecordInbound(
        int seq, string traceId, string method, string path,
        IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        if (!Enabled) return;
        _io.LogInboundRequest(seq, traceId, method, path, headers, body, body.Length);
    }

    /// <summary>Emit the <c>upstream-req</c> artifact. No-op when off.</summary>
    public void RecordUpstreamRequest(
        int seq, string traceId, string method, string url,
        IReadOnlyDictionary<string, string> headers, byte[] body, int bodyLength)
    {
        if (!Enabled) return;
        _io.LogUpstreamRequest(seq, traceId, method, url, headers, body, bodyLength);
    }

    /// <summary>Emit the <c>upstream-resp</c> artifact. No-op when off.</summary>
    public void RecordUpstreamResponse(
        int seq, string traceId, int status,
        IReadOnlyDictionary<string, string> headers, byte[] body, int bodyLength,
        string? error = null)
    {
        if (!Enabled) return;
        _io.LogUpstreamResponse(seq, traceId, status, headers, body, bodyLength, error: error);
    }

    /// <summary>Emit the <c>inbound-resp</c> artifact. No-op when off.</summary>
    public void RecordInboundResponse(
        int seq, string traceId, int status,
        IReadOnlyDictionary<string, string> headers, byte[] body, int bodyLength,
        IReadOnlyList<CapturedSseEvent>? events = null, string? error = null, long? durationMs = null)
    {
        if (!Enabled) return;
        _io.LogInboundResponse(
            seq, traceId, status, headers, body, bodyLength,
            events: events, error: error, durationMs: durationMs);
    }
}

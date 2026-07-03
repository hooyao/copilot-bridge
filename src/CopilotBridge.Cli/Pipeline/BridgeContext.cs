using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Mutable state that flows through every stage of one request. Stages mutate
/// <see cref="Request"/>, the strategy populates <see cref="Response"/>, and
/// downstream stages mutate <see cref="Response"/>. <see cref="Target"/> is
/// resolved by the model router stage early in the request pipeline; the
/// strategy registry consults it.
/// </summary>
internal sealed class BridgeContext<TBody> where TBody : class
{
    public required BridgeRequest<TBody> Request { get; init; }
    public required BridgeResponse Response { get; init; }
    public required CancellationToken Ct { get; init; }

    /// <summary>
    /// Resolved upstream destination. Set by the model-router stage; until
    /// that runs, accessing this throws (the runner enforces ordering).
    /// </summary>
    public RouteTarget? Target { get; set; }

    /// <summary>
    /// Side-channel for response stages that drop or rewrite SSE events but
    /// still want them surfaced in the inbound-resp audit (so a reader can
    /// see what the bridge filtered out). The endpoint reads this after the
    /// stream completes and merges it into the audit record.
    /// </summary>
    public List<DroppedSseEvent> DroppedEvents { get; init; } = [];

    /// <summary>
    /// Parsed inbound <c>anthropic-beta</c> tokens. Populated by the endpoint
    /// before the pipeline runs (the wire header is CSV; this is the split set,
    /// case-insensitive). Used by <c>ModelRouteResolver</c> to match rules with
    /// <c>Match.InboundBeta</c>, and by <c>HeadersOutboundStage</c> to forward
    /// tokens verbatim (pass-through-by-default policy — see
    /// <c>docs/pipeline-design.md §7.5</c>).
    /// </summary>
    public IReadOnlySet<string> InboundBetas { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Strip patterns accumulated when routing rules fire. Each pattern may end
    /// in <c>*</c> (trailing wildcard). <c>HeadersOutboundStage</c> applies them
    /// against the merged outbound beta list right before writing the header.
    /// </summary>
    public List<string> PendingBetaStrips { get; init; } = [];

    /// <summary>
    /// Extra <c>anthropic-beta</c> tokens to add to the outbound set. Populated
    /// by <c>ModelRouteResolver</c> when a matched location's
    /// <c>Use.Headers.Set["anthropic-beta"]</c> declares additional tokens;
    /// merged in <c>HeadersOutboundStage</c> before strip patterns run.
    /// </summary>
    public List<string> PendingBetaAdds { get; init; } = [];

    /// <summary>
    /// Per-request overrides for the Copilot identity header set built by
    /// <c>CopilotHeaderFactory.ApplyTo</c> (e.g. <c>Editor-Version</c>,
    /// <c>Editor-Plugin-Version</c>, <c>Copilot-Integration-Id</c>). Populated
    /// by <c>ModelRouteResolver</c> when a location's <c>Use.Headers.Set</c>
    /// targets one of those whitelisted names; the strategy threads the dict
    /// into the <c>CopilotClient</c> call. Case-insensitive keys. <c>null</c>
    /// values mean "remove this header" (so a location can opt out of an
    /// identity header rather than only overriding it).
    /// </summary>
    public Dictionary<string, string?> CopilotHeaderOverrides { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The model id the client put on the wire, captured BEFORE any pipeline
    /// rewrite (normalize / user-routing / profile variant). Set by
    /// <c>ModelRouterStage</c> as its very first step; <c>ResponseModelRewriteStage</c>
    /// reads it back at the end to restore the original name in the response
    /// body and the <c>message_start</c> SSE event, so client-side accounting
    /// (ccusage, Claude Code's own session log) keeps reporting the model the
    /// user actually asked for — not the back-end variant the bridge routed to.
    /// Null until the model router runs.
    /// </summary>
    public string? OriginalRequestedModel { get; set; }

    /// <summary>
    /// Set true by the tool-leak guard (<c>ResponseInspectionStage</c> /
    /// <c>ToolLeakDetector</c>) when it detects a leaked tool call and forces a
    /// retry. The endpoint copies it into the per-request summary
    /// (<c>toolLeakDetected</c>) so the real-world leak rate is measurable.
    /// For streaming this is set mid-relay (the endpoint reads it after the
    /// stream drains); for buffered it is set during the stage.
    /// </summary>
    public bool ToolLeakDetected { get; set; }

    /// <summary>
    /// Per-request trace id (<c>BridgeIoSeq.BuildTraceId</c>) — the same id that
    /// names the four <c>&lt;traceId&gt;-*.json</c> trace files and prefixes every
    /// in-request log line as <c>[&lt;traceId&gt;] </c>. Set by the pipeline-driving
    /// endpoints (<c>ClaudeCodeMessagesEndpoint</c> and <c>CodexResponsesEndpoint</c>)
    /// before the pipeline runs; each also pushes it onto Serilog's <c>LogContext</c>
    /// so every stage/detector log line emitted during the request is correlated
    /// to the trace. Null if an endpoint didn't set it (e.g. a unit test driving
    /// the runner directly).
    /// </summary>
    public string? TraceId { get; set; }
}

/// <summary>One SSE event a response stage chose not to forward downstream.</summary>
internal readonly record struct DroppedSseEvent(string? EventType, string Data);

/// <summary>
/// The inbound side of <see cref="BridgeContext{TBody}"/>: typed body, mutable
/// headers (which become the upstream HTTP headers after the
/// <c>HeadersOutboundStage</c>), and the original raw bytes for audit.
/// </summary>
internal sealed class BridgeRequest<TBody> where TBody : class
{
    public required string Method { get; init; }
    public required string Path { get; init; }

    /// <summary>Original inbound bytes. Read-only; preserved for the audit log.</summary>
    public ReadOnlyMemory<byte> RawBody { get; init; }

    /// <summary>Typed body — IR shape inside the pipeline; stages mutate via <c>with</c>-expressions or in place.</summary>
    public required TBody Body { get; set; }

    /// <summary>
    /// Mutable headers. Inbound headers populate this; stages
    /// add/remove/rename; the strategy reads the final state when constructing
    /// the upstream HTTP request.
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The outbound side of <see cref="BridgeContext{TBody}"/>. Either
/// <see cref="EventStream"/> (streaming) or <see cref="BufferedBody"/>
/// (non-streaming) is populated based on <see cref="Mode"/>; stages may wrap
/// or replace whichever applies.
/// </summary>
internal sealed class BridgeResponse
{
    public int Status { get; set; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public ResponseMode Mode { get; set; } = ResponseMode.Streaming;

    /// <summary>
    /// Streaming response payload. Stages wrap with transforming iterators
    /// (drop-by-predicate, transform-event, capture-for-log). The final
    /// consumer (the endpoint writer) drives the chain by enumerating.
    /// </summary>
    public IAsyncEnumerable<SseItem<string>>? EventStream { get; set; }

    /// <summary>Non-streaming response payload. Stages parse / mutate / re-serialize.</summary>
    public byte[]? BufferedBody { get; set; }

    /// <summary>
    /// The exact bytes a translating strategy POSTed upstream (Codex /responses
    /// T2 body). Null on passthrough paths (/cc), where the IR body IS the wire
    /// body. Lets the endpoint audit the real wire bytes, not the IR.
    /// </summary>
    public byte[]? UpstreamWireBody { get; set; }

    /// <summary>
    /// RAW upstream response bytes for the BUFFERED path, captured by the
    /// strategy BEFORE any response stage rewrites <see cref="BufferedBody"/>.
    /// It is the same array reference the strategy first assigned to
    /// <see cref="BufferedBody"/>; <c>ResponseModelRewriteStage</c> reassigns
    /// <see cref="BufferedBody"/> to a NEW array when it rewrites the model, so
    /// this keeps pointing at Copilot's original wire bytes. The endpoint
    /// prefers this over <see cref="BufferedBody"/> when writing the
    /// <c>upstream-resp</c> audit. Null when tracing is disabled or on the
    /// streaming path (see <see cref="RawUpstreamResponseCapture"/>).
    /// </summary>
    public byte[]? RawUpstreamResponseBody { get; set; }

    /// <summary>
    /// RAW upstream response capture for the STREAMING path. The strategy tees
    /// the network stream into this as <c>SseParser</c> reads it, so it ends up
    /// holding the exact SSE bytes Copilot sent — pre-stage, pre-translation.
    /// Filled lazily by the relay loop; the endpoint reads it in its
    /// <c>finally</c> AFTER enumeration completes (the buffer is fully
    /// populated by then). Null when tracing is disabled or on the buffered
    /// path (see <see cref="RawUpstreamResponseBody"/>).
    /// </summary>
    public RawResponseCapture? RawUpstreamResponseCapture { get; set; }

    /// <summary>
    /// A mid-stream upstream fault that a streaming strategy CAUGHT internally
    /// (rather than letting propagate) so it could still flush a terminal event.
    /// The Codex <c>/responses</c> strategy does this: it latches a transient
    /// disconnect into a synthetic <c>response.failed</c> terminal and returns
    /// normally, so no exception reaches the endpoint's catch. The endpoint folds
    /// this into the audit's <c>error</c> field after draining the stream, so a
    /// truncated <c>upstream-resp</c> isn't logged as a clean success. Null when
    /// the stream completed without a caught fault. (The <c>/cc</c> passthrough
    /// strategy does NOT catch — its faults propagate to the endpoint directly —
    /// so it leaves this null.)
    /// </summary>
    public Exception? UpstreamStreamFault { get; set; }

    /// <summary>
    /// The bytes to record as the <c>upstream-resp</c> audit body — Copilot's RAW
    /// response, pre-stage. Prefers the buffered pre-rewrite array
    /// (<see cref="RawUpstreamResponseBody"/>), then the finalized streaming
    /// capture (<see cref="RawUpstreamResponseCapture"/>), then the post-stage
    /// <see cref="BufferedBody"/> as a defensive fallback (only reached if a
    /// strategy populated neither raw field — e.g. tracing off, where the audit
    /// is not written anyway). Returns null only if all three are null.
    /// </summary>
    /// <remarks>
    /// <b>Side effect:</b> reading the streaming capture finalizes it
    /// (<see cref="RawResponseCapture.ToArray"/> seals the buffer). Call this once,
    /// from the endpoint's <c>finally</c>, AFTER the stream has been fully drained
    /// by the relay loop — never mid-stream.
    /// </remarks>
    public byte[]? RawUpstreamRespBytesOrNull() =>
        RawUpstreamResponseBody ?? RawUpstreamResponseCapture?.ToArray() ?? BufferedBody;
}

internal enum ResponseMode { Streaming, Buffered }

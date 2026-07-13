using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Mutable state that flows through every stage of one request. Stages mutate
/// <see cref="Request"/>, the strategy populates <see cref="Response"/>, and
/// downstream stages mutate <see cref="Response"/>. <see cref="Target"/> is
/// resolved by the model router stage early in the request pipeline; the
/// strategy registry consults it.
/// </summary>
/// <remarks>
/// This is a <b>scoped DI service</b> (one per request scope). The container
/// constructs an empty shell; the pipeline-driving endpoint then populates
/// <see cref="Request"/>, <see cref="Response"/>, <see cref="Ct"/>,
/// <see cref="InboundBetas"/>, and <see cref="TraceId"/> before running the
/// pipeline. Consequently the <b>constructors of injected components (stages,
/// strategies, adapters, detectors) MUST NOT read <see cref="Request"/></b> — it
/// is unpopulated at construction time. Request data is read only during
/// <c>ApplyAsync</c>/<c>ForwardAsync</c>/<c>Begin</c>, which the runner invokes
/// strictly after the endpoint has filled the shell.
/// </remarks>
internal sealed class BridgeContext<TBody> where TBody : class
{
    // Settable (not required-init) because the DI shell is filled by the endpoint
    // after construction — see the class remarks. Non-null defaults (= null!) mark
    // "populated before first read"; a constructor that reads Request before the
    // endpoint fills it is a bug the remarks call out.
    public BridgeRequest<TBody> Request { get; set; } = null!;
    public BridgeResponse Response { get; set; } = null!;
    public CancellationToken Ct { get; set; }

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
    public IReadOnlySet<string> InboundBetas { get; set; } =
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
    /// Set true by the response-leak guard (<c>ResponseInspectionStage</c> /
    /// <c>ResponseLeakDetector</c>) when it detects a leak — a leaked tool call or a
    /// leaked control envelope — and forces a retry. The endpoint copies it into the
    /// per-request summary (<c>responseLeakDetected</c>) so the real-world leak rate
    /// is measurable. For streaming this is set mid-relay (the endpoint reads it after
    /// the stream drains); for buffered it is set during the stage.
    /// </summary>
    public bool ResponseLeakDetected { get; set; }

    /// <summary>
    /// Set true by the runaway guard (<c>RunawayGuardDetector</c>) when a streamed
    /// response exceeds its byte / delta-count budget — the degenerate-generation
    /// signature (e.g. a model stuck emitting tens of thousands of tiny tool-arg
    /// fragments). The guard aborts the turn with a retryable error; the endpoint
    /// copies this into the per-request summary (<c>runaway</c>) so trips are
    /// grep-able and the rate is measurable. Distinct from
    /// <see cref="ResponseLeakDetected"/> (protocol-leak, not volume). Set by the
    /// detector's trip during stream relay / buffered scan.
    /// </summary>
    public bool RunawayDetected { get; set; }

    /// <summary>
    /// Set true by <c>ToolInputValidationDetector</c> when a real <c>tool_use</c>
    /// block closes (streamed) or is found (buffered) with malformed JSON input or
    /// input that violates the request's declared tool schema. The detector aborts to
    /// keep the bad block out of the client's context; the endpoint copies this into
    /// the per-request summary as <c>tool_input_invalid=</c>. Distinct from response
    /// leaks (tool calls emitted as text) and runaway output (volume).
    /// </summary>
    public bool ToolInputInvalidDetected { get; set; }

    /// <summary>
    /// Count of inbound <c>tool_result</c> blocks that carry a replayed API-error
    /// payload (content starting with <c>"API Error:"</c>) — failure debris from
    /// earlier failed tool / sub-agent calls in the same session that Claude Code
    /// keeps in the transcript and resends. A context heavily poisoned with these
    /// pushed gpt-5.5 into the degenerate runaway (<c>docs/gpt55-runaway-diagnosis.md</c>);
    /// no frontier model on the bridge can un-poison the client's transcript, so the
    /// only cure is the user compacting the session. Set by
    /// <c>PoisonedContextScanStage</c> during the request pipeline (0 when the stage is
    /// disabled or finds none); the endpoint copies it into the per-request summary
    /// (<c>poisoned_tool_results=</c>). COUNT ONLY — the transcript is never mutated
    /// (dropping a <c>tool_result</c> without its paired <c>tool_use</c> 400s upstream).
    /// </summary>
    public int PoisonedToolResults { get; set; }

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
    /// The exact bytes a strategy POSTed upstream, captured for the
    /// <c>upstream-req</c> audit: the passthrough Anthropic body on a <c>/cc</c>
    /// (CopilotAnthropic) route, or the Codex T2 Responses body on a
    /// <c>/responses</c> (CopilotResponses) route — the same array handed to the
    /// Copilot client, never a re-serialized IR. Both strategies stash it ONLY when
    /// tracing is on, so this is non-null iff tracing is enabled and a strategy ran;
    /// it means exactly "captured upstream wire bytes", nothing more. It is NOT a
    /// routing signal — which backend ran is read from <see cref="BridgeContext{T}.Target"/>'s
    /// <c>Vendor</c>, not inferred from this field being null.
    /// </summary>
    public byte[]? UpstreamWireBody { get; set; }

    /// <summary>
    /// The <c>reasoning.effort</c> the Codex strategy (T2) actually wrote to the
    /// wire after per-model coercion, or null when no effort was set. Distinct from
    /// the IR body's <c>OutputConfig.Effort</c>: on the Responses path coercion
    /// happens in <c>ResponsesRequestBuilder</c> and is NOT written back to the IR,
    /// so the endpoint reads the honest outbound value from here to log
    /// <c>effort=max→xhigh</c> instead of the un-coerced inbound <c>max</c>. Null on
    /// the Anthropic passthrough path (where effort coercion is done by
    /// <c>ProfileAdjuster</c> and already reflected on the IR body — the endpoint
    /// falls back to the IR body there). Not audit-gated: always set by the strategy.
    /// </summary>
    public string? OutboundEffortCoerced { get; set; }

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

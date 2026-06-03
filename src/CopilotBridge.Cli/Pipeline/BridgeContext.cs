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
}

internal enum ResponseMode { Streaming, Buffered }

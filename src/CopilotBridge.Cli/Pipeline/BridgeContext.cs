using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Hosting;

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
    public required BridgeRequestLog Log { get; init; }
    public required CancellationToken Ct { get; init; }

    /// <summary>
    /// Resolved upstream destination. Set by the model-router stage; until
    /// that runs, accessing this throws (the runner enforces ordering).
    /// </summary>
    public RouteTarget? Target { get; set; }
}

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

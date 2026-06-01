namespace CopilotBridge.Cli.Models.Anthropic.Errors;

/// <summary>
/// Anthropic's top-level error wire format
/// (<c>{"type":"error","error":{"type":...,"message":...}}</c>). The bridge
/// emits this for failures it generates itself — e.g. an inbound model with no
/// profile in <see cref="Pipeline.Routing.ModelProfileCatalog"/> — so a client
/// cannot tell the rejection came from the bridge rather than the real
/// Anthropic API. The human-readable diagnostics live in
/// <see cref="ErrorBody.Message"/>, prefixed <c>[copilot-bridge]</c> so a
/// reader scanning client logs can still attribute it.
/// </summary>
internal sealed record ErrorResponse
{
    public string Type { get; init; } = "error";
    public required ErrorBody Error { get; init; }
}

/// <summary>
/// The inner <c>error</c> object. <see cref="Type"/> is one of Anthropic's
/// documented error type strings (<c>invalid_request_error</c>,
/// <c>not_supported</c>, …) so clients route it through their existing
/// error-type handling rather than crashing on an unknown shape.
/// </summary>
internal sealed record ErrorBody
{
    public required string Type { get; init; }
    public required string Message { get; init; }
}

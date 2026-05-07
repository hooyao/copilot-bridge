namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Mutable per-request capture used by the bridge logger. The endpoint populates
/// fields as it processes the request and hands the whole record to
/// <see cref="BridgeRequestLogger.WriteAsync"/> on completion.
/// </summary>
internal sealed class BridgeRequestLog
{
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public long DurationMs { get; set; }

    // Inbound (Claude Code → bridge).
    public string? Method { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string> InboundHeaders { get; init; } = [];
    public string? InboundBody { get; set; }

    // Upstream (bridge → Copilot). Empty for endpoints that don't proxy
    // (e.g. count_tokens, /v1/models's local projection).
    public string? UpstreamUrl { get; set; }
    public Dictionary<string, string> UpstreamHeaders { get; init; } = [];
    public string? UpstreamBody { get; set; }
    public int UpstreamStatus { get; set; }
    public Dictionary<string, string> UpstreamResponseHeaders { get; init; } = [];

    // Body (non-streaming) or accumulated SSE events (streaming).
    public string? DownstreamBody { get; set; }
    public List<SseEventCapture> Events { get; init; } = [];

    // Optional summary of any error encountered during proxying.
    public string? Error { get; set; }
}

/// <summary>
/// Captures one SSE event. <c>Filtered</c> = true means the bridge dropped the
/// event before forwarding (currently only the OpenAI-style <c>[DONE]</c>).
/// </summary>
internal sealed record SseEventCapture(string? EventType, string Data, bool Filtered);

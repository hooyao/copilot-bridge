using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Common;

namespace CopilotBridge.Cli.Models.Anthropic.Response;

/// <summary>
/// Mirrors <c>BetaMessage</c> — the response body of <c>POST /v1/messages</c>
/// (non-streaming) and the payload of the <c>message_start</c> stream event.
/// Server-tool-only fields (<c>container</c>) are kept as <see cref="JsonElement"/>
/// for passthrough.
/// </summary>
internal sealed record AnthropicMessage
{
    public required string Id { get; init; }
    public string Type { get; init; } = "message";
    public string Role { get; init; } = "assistant";

    /// <summary>Canonicalized form (e.g. request <c>claude-sonnet-4.6</c> → response <c>claude-sonnet-4-6</c>).</summary>
    public required string Model { get; init; }

    public required IReadOnlyList<ContentBlock> Content { get; init; }

    public string? StopReason { get; init; }
    public string? StopSequence { get; init; }
    public RefusalStopDetails? StopDetails { get; init; }

    public required Usage Usage { get; init; }

    public ContextManagementResponse? ContextManagement { get; init; }

    /// <summary>Server-tool container info; null in Claude-Code-shaped responses.</summary>
    public JsonElement? Container { get; init; }

    /// <summary>
    /// Captures Copilot extension fields the SDK doesn't model:
    /// <c>amazon-bedrock-invocationMetrics</c> (in <c>message_stop</c>),
    /// <c>copilot_usage</c>, <c>copilot_annotations</c>. Keeps round-trip
    /// fidelity for M3 translation. Uses <c>set</c> rather than <c>init</c>
    /// because <c>JsonExtensionData</c> requires a post-construction setter.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

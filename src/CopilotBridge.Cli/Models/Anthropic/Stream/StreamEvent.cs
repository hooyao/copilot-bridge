using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Common;
using CopilotBridge.Cli.Models.Anthropic.Response;

namespace CopilotBridge.Cli.Models.Anthropic.Stream;

/// <summary>
/// Mirrors <c>BetaRawMessageStreamEvent</c>. M1's bridge streams these as raw
/// SSE bytes (no deserialization on the response path), but the typed shape is
/// already required for M3 (translation to OpenAI) and is convenient for the
/// playground.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageStartEvent), "message_start")]
[JsonDerivedType(typeof(MessageDeltaEvent), "message_delta")]
[JsonDerivedType(typeof(MessageStopEvent), "message_stop")]
[JsonDerivedType(typeof(ContentBlockStartEvent), "content_block_start")]
[JsonDerivedType(typeof(ContentBlockDeltaEvent), "content_block_delta")]
[JsonDerivedType(typeof(ContentBlockStopEvent), "content_block_stop")]
internal abstract record StreamEvent;

internal sealed record MessageStartEvent : StreamEvent
{
    public required AnthropicMessage Message { get; init; }
}

internal sealed record MessageDeltaEvent : StreamEvent
{
    public required MessageDeltaInfo Delta { get; init; }
    public required MessageDeltaUsage Usage { get; init; }
    public ContextManagementResponse? ContextManagement { get; init; }

    /// <summary>Catches <c>copilot_usage</c> and any other Copilot extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// Mirrors <c>BetaRawMessageDeltaEvent.Delta</c> — the inner delta object
/// carrying the final stop reason for the request.
/// </summary>
internal sealed record MessageDeltaInfo
{
    public string? StopReason { get; init; }
    public string? StopSequence { get; init; }
    public RefusalStopDetails? StopDetails { get; init; }
    public JsonElement? Container { get; init; }
}

internal sealed record MessageStopEvent : StreamEvent
{
    /// <summary>
    /// Catches <c>amazon-bedrock-invocationMetrics</c> (research §4.1 event 17).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

internal sealed record ContentBlockStartEvent : StreamEvent
{
    public int Index { get; init; }
    public required ContentBlock ContentBlock { get; init; }
}

internal sealed record ContentBlockDeltaEvent : StreamEvent
{
    public int Index { get; init; }
    public required ContentBlockDelta Delta { get; init; }
}

internal sealed record ContentBlockStopEvent : StreamEvent
{
    public int Index { get; init; }
}

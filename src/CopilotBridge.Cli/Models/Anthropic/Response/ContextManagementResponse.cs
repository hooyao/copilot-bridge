using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Response;

/// <summary>
/// Mirrors <c>BetaContextManagementResponse</c>. Reports which edits were applied
/// during the request — appears in the response body and on
/// <c>message_delta</c> stream events.
/// </summary>
internal sealed record ContextManagementResponse
{
    public required IReadOnlyList<ContextEditResponse> AppliedEdits { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClearToolUses20250919EditResponse), "clear_tool_uses_20250919")]
[JsonDerivedType(typeof(ClearThinking20251015EditResponse), "clear_thinking_20251015")]
internal abstract record ContextEditResponse;

internal sealed record ClearToolUses20250919EditResponse : ContextEditResponse
{
    public int ClearedInputTokens { get; init; }
    public int ClearedToolUses { get; init; }
}

internal sealed record ClearThinking20251015EditResponse : ContextEditResponse
{
    public int ClearedInputTokens { get; init; }
    public int ClearedThinkingTurns { get; init; }
}

/// <summary>
/// Token-counting variant — <c>BetaCountTokensContextManagementResponse</c>.
/// Used by <c>POST /v1/messages/count_tokens</c>; M1 does not populate it.
/// </summary>
internal sealed record CountTokensContextManagementResponse
{
    public int OriginalInputTokens { get; init; }
}

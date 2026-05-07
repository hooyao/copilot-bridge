namespace CopilotBridge.Cli.Models.Anthropic.Models;

/// <summary>
/// Mirrors Anthropic SDK's <c>ModelInfo</c>. Capabilities are intentionally not
/// modeled — the bridge synthesizes this from Copilot's <c>/models</c> response
/// and Claude Code only consumes <c>id</c>/<c>display_name</c> for selection.
/// </summary>
internal sealed record AnthropicModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>RFC 3339 datetime; epoch fallback when unknown.</summary>
    public required string CreatedAt { get; init; }

    public string Type { get; init; } = "model";

    public int? MaxInputTokens { get; init; }
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Mirrors <c>ModelInfosPage</c> — a single non-paginated page of models. Bridge
/// always returns <c>has_more: false</c> with no cursor, since the model list is
/// fixed per Copilot account.
/// </summary>
internal sealed record AnthropicModelsResponse
{
    public required IReadOnlyList<AnthropicModelInfo> Data { get; init; }
    public bool HasMore { get; init; }
    public string? FirstId { get; init; }
    public string? LastId { get; init; }
}

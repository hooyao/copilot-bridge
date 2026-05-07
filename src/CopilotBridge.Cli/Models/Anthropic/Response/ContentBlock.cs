using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Response;

/// <summary>
/// Mirrors <c>BetaContentBlock</c> (response variants). Server-side tool blocks
/// (web_search / web_fetch / advisor / code_execution / bash / text_editor /
/// tool_search / mcp / container_upload / compaction) are not modeled — Claude
/// Code's bridge usage doesn't surface them in M1.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(RedactedThinkingBlock), "redacted_thinking")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
internal abstract record ContentBlock;

internal sealed record TextBlock : ContentBlock
{
    public required string Text { get; init; }
    /// <summary>Citations array (often null for Copilot-served Claude responses).</summary>
    public JsonElement? Citations { get; init; }
}

internal sealed record ThinkingBlock : ContentBlock
{
    public required string Thinking { get; init; }
    public required string Signature { get; init; }
}

internal sealed record RedactedThinkingBlock : ContentBlock
{
    public required string Data { get; init; }
}

internal sealed record ToolUseBlock : ContentBlock
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Input { get; init; }
}

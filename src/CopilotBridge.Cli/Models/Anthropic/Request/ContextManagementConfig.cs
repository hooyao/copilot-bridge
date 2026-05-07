using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaContextManagementConfig</c>. Pairs with the
/// <c>context-management-2025-06-27</c> beta header (research §15.4).
/// </summary>
internal sealed record ContextManagementConfig
{
    public IReadOnlyList<ContextEdit>? Edits { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClearToolUses20250919Edit), "clear_tool_uses_20250919")]
[JsonDerivedType(typeof(ClearThinking20251015Edit), "clear_thinking_20251015")]
[JsonDerivedType(typeof(Compact20260112Edit), "compact_20260112")]
internal abstract record ContextEdit;

/// <summary>
/// Mirrors <c>BetaClearToolUses20250919Edit</c>. <c>keep</c> and <c>trigger</c>
/// have nested-object shapes whose keys are themselves discriminators; left as
/// <see cref="JsonElement"/> for passthrough fidelity.
/// </summary>
internal sealed record ClearToolUses20250919Edit : ContextEdit
{
    public JsonElement? ClearAtLeast { get; init; }
    /// <summary><c>boolean | string[]</c> in the SDK; opaque here.</summary>
    public JsonElement? ClearToolInputs { get; init; }
    public IReadOnlyList<string>? ExcludeTools { get; init; }
    public JsonElement? Keep { get; init; }
    public JsonElement? Trigger { get; init; }
}

/// <summary>Mirrors <c>BetaClearThinking20251015Edit</c>.</summary>
internal sealed record ClearThinking20251015Edit : ContextEdit
{
    /// <summary><c>BetaThinkingTurns | BetaAllThinkingTurns | "all"</c>.</summary>
    public JsonElement? Keep { get; init; }
}

/// <summary>
/// Mirrors <c>BetaCompact20260112Edit</c>. Server-internal edit; modeled as an
/// empty placeholder for SDK fidelity. Claude Code does not send it.
/// </summary>
internal sealed record Compact20260112Edit : ContextEdit;

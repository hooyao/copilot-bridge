using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaToolChoice</c>. Forced-tool variants (<c>any</c>/<c>tool</c>) are
/// incompatible with thinking — preprocessing disables them when thinking is on
/// (research §3.6 rule 4).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ToolChoiceAuto), "auto")]
[JsonDerivedType(typeof(ToolChoiceAny), "any")]
[JsonDerivedType(typeof(ToolChoiceTool), "tool")]
[JsonDerivedType(typeof(ToolChoiceNone), "none")]
internal abstract record ToolChoice;

internal sealed record ToolChoiceAuto : ToolChoice
{
    public bool? DisableParallelToolUse { get; init; }
}

internal sealed record ToolChoiceAny : ToolChoice
{
    public bool? DisableParallelToolUse { get; init; }
}

internal sealed record ToolChoiceTool : ToolChoice
{
    public required string Name { get; init; }
    public bool? DisableParallelToolUse { get; init; }
}

internal sealed record ToolChoiceNone : ToolChoice;

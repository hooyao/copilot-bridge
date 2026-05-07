using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Stream;

/// <summary>
/// Mirrors <c>BetaRawContentBlockDelta</c>. The <c>citations_delta</c> and
/// <c>compaction_content_block_delta</c> variants are not modeled (they are
/// server-side; Claude Code never produces them and Copilot doesn't emit them
/// for our usage).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextDelta), "text_delta")]
[JsonDerivedType(typeof(InputJsonDelta), "input_json_delta")]
[JsonDerivedType(typeof(ThinkingDelta), "thinking_delta")]
[JsonDerivedType(typeof(SignatureDelta), "signature_delta")]
internal abstract record ContentBlockDelta;

internal sealed record TextDelta : ContentBlockDelta
{
    public required string Text { get; init; }
}

/// <summary>
/// Tool-call argument fragment. Anthropic streams each fragment of the JSON
/// argument as <c>partial_json</c>; clients accumulate.
/// </summary>
internal sealed record InputJsonDelta : ContentBlockDelta
{
    public required string PartialJson { get; init; }
}

internal sealed record ThinkingDelta : ContentBlockDelta
{
    public required string Thinking { get; init; }
}

/// <summary>
/// Signature attached to a thinking block. Emitted as a separate event after the
/// thinking text deltas (research §4.1 event 6).
/// </summary>
internal sealed record SignatureDelta : ContentBlockDelta
{
    public required string Signature { get; init; }
}

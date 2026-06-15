using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Common;
using CopilotBridge.Cli.Models.Common;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaContentBlockParam</c>. The variants Claude Code actually emits;
/// server-side tool variants (web_search / code_execution / computer_use / MCP /
/// container / advisor / tool_search) are intentionally not modeled — they are
/// not in the request path the bridge serves.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlockParam), "text")]
[JsonDerivedType(typeof(ImageBlockParam), "image")]
[JsonDerivedType(typeof(DocumentBlockParam), "document")]
[JsonDerivedType(typeof(ToolUseBlockParam), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlockParam), "tool_result")]
[JsonDerivedType(typeof(ThinkingBlockParam), "thinking")]
[JsonDerivedType(typeof(RedactedThinkingBlockParam), "redacted_thinking")]
internal abstract record ContentBlockParam
{
    /// <summary>
    /// Part-level namespaced escape-hatch (<c>docs/ir-definition-design.md</c>
    /// §3.2). Defined on the base so every variant inherits it; carries
    /// per-part provider data the Anthropic block shape can't type (e.g. a
    /// Responses item's <c>id</c>/<c>encrypted_content</c> when not expressible
    /// as a thinking block). <c>null</c> for every Claude Code block — and
    /// <c>WhenWritingNull</c> then omits it, so existing blocks serialize
    /// byte-identically. Defined for symmetry / future Gemini; MAY ship unused.
    /// </summary>
    public ProviderExtensions? ProviderExtensions { get; init; }
}

internal sealed record TextBlockParam : ContentBlockParam
{
    public required string Text { get; init; }
    public CacheControl? CacheControl { get; init; }
}

internal sealed record ImageBlockParam : ContentBlockParam
{
    public required ImageSource Source { get; init; }
    public CacheControl? CacheControl { get; init; }
}

/// <summary>Mirrors <c>BetaRequestDocumentBlock</c>.</summary>
internal sealed record DocumentBlockParam : ContentBlockParam
{
    public required DocumentSource Source { get; init; }
    public string? Title { get; init; }
    public string? Context { get; init; }
    public CacheControl? CacheControl { get; init; }
}

internal sealed record ToolUseBlockParam : ContentBlockParam
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Opaque tool input — JSON object whose schema is the tool definition's <c>input_schema</c>.</summary>
    public required JsonElement Input { get; init; }
    public CacheControl? CacheControl { get; init; }
}

internal sealed record ToolResultBlockParam : ContentBlockParam
{
    public required string ToolUseId { get; init; }
    /// <summary>
    /// Anthropic spec: <c>string | Array&lt;TextBlockParam | ImageBlockParam | ...&gt;</c>.
    /// Held as <see cref="JsonElement"/>; preprocessing's
    /// <c>mergeToolResultForClaude</c> rule operates on this opaquely.
    /// </summary>
    public JsonElement? Content { get; init; }
    public bool? IsError { get; init; }
    public CacheControl? CacheControl { get; init; }
}

internal sealed record ThinkingBlockParam : ContentBlockParam
{
    public required string Thinking { get; init; }
    public required string Signature { get; init; }
}

internal sealed record RedactedThinkingBlockParam : ContentBlockParam
{
    public required string Data { get; init; }
}

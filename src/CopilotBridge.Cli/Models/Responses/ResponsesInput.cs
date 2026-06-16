using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Responses;

/// <summary>
/// One item in the Responses <c>input[]</c> array. Polymorphic by <c>type</c>:
/// a conversation <c>message</c>, a <c>function_call</c> (assistant tool call), a
/// <c>function_call_output</c> (tool result), or a <c>reasoning</c> item (carries
/// <c>encrypted_content</c> for multi-turn). Mirrors the OpenAI SDK
/// <c>ResponseInputItem</c> union, modeling only the variants Codex emits
/// (research §3.2 / the real captures).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ResponsesMessageItem), "message")]
[JsonDerivedType(typeof(ResponsesFunctionCallItem), "function_call")]
[JsonDerivedType(typeof(ResponsesFunctionCallOutputItem), "function_call_output")]
[JsonDerivedType(typeof(ResponsesReasoningItem), "reasoning")]
internal abstract record ResponsesInputItem;

/// <summary>
/// A conversation message. <c>role</c> is <c>user | assistant | developer |
/// system</c> (Codex uses <c>developer</c> for its harness preamble — T1 maps it
/// to IR <c>system</c>). <c>content</c> is an array of typed parts.
/// </summary>
internal sealed record ResponsesMessageItem : ResponsesInputItem
{
    public required string Role { get; init; }
    public required IReadOnlyList<ResponsesContentPart> Content { get; init; }
}

/// <summary>
/// An assistant tool call. Maps to IR <c>tool_use</c>: <c>call_id</c> ↔ <c>id</c>,
/// <c>arguments</c> (a JSON STRING on the Responses wire) ↔ IR <c>input</c> (a
/// JSON object). T1/T2 convert between the string and object forms; the bytes of
/// the underlying JSON are preserved.
/// </summary>
internal sealed record ResponsesFunctionCallItem : ResponsesInputItem
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    /// <summary>JSON-encoded arguments STRING (e.g. <c>"{\"command\":\"ls\"}"</c>).</summary>
    public required string Arguments { get; init; }
    public string? Id { get; init; }
    public string? Status { get; init; }
}

/// <summary>
/// A tool result. Maps to IR <c>tool_result</c>: <c>call_id</c> ↔ <c>tool_use_id</c>,
/// <c>output</c> ↔ the tool-result content. <c>output</c> may be a string or a
/// structured value, held opaque.
/// </summary>
internal sealed record ResponsesFunctionCallOutputItem : ResponsesInputItem
{
    public required string CallId { get; init; }
    public required JsonElement Output { get; init; }
    public string? Id { get; init; }
    public string? Status { get; init; }
}

/// <summary>
/// A reasoning item carrying <c>encrypted_content</c> — Codex echoes this back on
/// multi-turn requests (<c>include:["reasoning.encrypted_content"]</c>). Maps to
/// IR reasoning where it fits, else rides the part-level bag (the §7 round-trip
/// test decides). Fields beyond the opaque blob are kept for fidelity.
/// </summary>
internal sealed record ResponsesReasoningItem : ResponsesInputItem
{
    public string? Id { get; init; }
    /// <summary>Opaque encrypted reasoning blob — byte-faithful, never mutated.</summary>
    public string? EncryptedContent { get; init; }
    /// <summary>Summary parts, if present — held opaque.</summary>
    public JsonElement? Summary { get; init; }
    public JsonElement? Content { get; init; }
}

/// <summary>
/// A content part inside a <c>message</c>. Polymorphic by <c>type</c>:
/// <c>input_text</c> / <c>output_text</c> (text) or <c>input_image</c> (a data
/// URL). Mirrors the OpenAI SDK content-part union, modeling the variants Codex
/// emits (research §3.2 / captures show input_text + output_text; input_image is
/// the vision path).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ResponsesInputTextPart), "input_text")]
[JsonDerivedType(typeof(ResponsesOutputTextPart), "output_text")]
[JsonDerivedType(typeof(ResponsesInputImagePart), "input_image")]
internal abstract record ResponsesContentPart;

internal sealed record ResponsesInputTextPart : ResponsesContentPart
{
    public required string Text { get; init; }
}

internal sealed record ResponsesOutputTextPart : ResponsesContentPart
{
    public required string Text { get; init; }
    /// <summary>Citations/annotations on assistant text — held opaque if present.</summary>
    public JsonElement? Annotations { get; init; }
}

/// <summary>
/// An image input. <c>image_url</c> is a <c>data:image/…;base64,…</c> URL on the
/// Responses wire (Codex strips <c>detail</c> under responses-lite). Maps to IR
/// <c>image</c> with a base64 source.
/// </summary>
internal sealed record ResponsesInputImagePart : ResponsesContentPart
{
    public required string ImageUrl { get; init; }
    public string? Detail { get; init; }
}

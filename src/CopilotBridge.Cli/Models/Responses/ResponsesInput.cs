using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Responses;

/// <summary>
/// One item in the Responses <c>input[]</c> array. Polymorphic by <c>type</c>:
/// a conversation <c>message</c>, a <c>function_call</c> (assistant tool call), a
/// <c>function_call_output</c> (tool result), or a <c>reasoning</c> item (carries
/// <c>encrypted_content</c> for multi-turn). Every OTHER type — the
/// <c>additional_tools</c> harness preamble, <c>agent_message</c> (gpt-5.6 inter-agent),
/// <c>tool_search_call</c>, <c>custom_tool_call</c>, <c>compaction</c>, … — is carried
/// opaquely as a <see cref="ResponsesUnknownItem"/> and re-emitted verbatim (see the
/// remarks). Mirrors the OpenAI SDK <c>ResponseInputItem</c> union (research §3.2 / the
/// real captures + openai/codex <c>ResponseItem.ts</c>).
/// </summary>
/// <remarks>
/// The Responses <c>input[]</c> union is OPEN-ENDED — the gpt-5.6 multi-agent /
/// tool-search / compaction features keep adding item types (openai/codex
/// <c>ResponseItem.ts</c> lists <c>agent_message</c>, <c>tool_search_call</c>,
/// <c>custom_tool_call</c>, <c>custom_tool_call_output</c>, <c>web_search_call</c>,
/// <c>image_generation_call</c>, <c>compaction</c>, <c>context_compaction</c>,
/// <c>local_shell_call</c>, … beyond what the bridge models). A closed
/// <c>[JsonPolymorphic]</c> whitelist 400s (<c>Polymorphism_UnrecognizedTypeDiscriminator</c>)
/// on ANY unmodeled type BEFORE T1 even runs — the whack-a-mole that shipped four
/// gpt-5.6 tool bugs. So deserialization goes through
/// <see cref="Converters.ResponsesInputItemListConverter"/>: known <c>type</c>s bind
/// to their records via the source generator; an UNKNOWN <c>type</c> is captured
/// whole as a <see cref="ResponsesUnknownItem"/> (opaque, byte-faithful) and re-emitted
/// verbatim by T2 — so a new item type is carried through, never rejected.
/// <para>Only types the bridge actually INTERPRETS are modeled here. Opaque items the
/// bridge merely FORWARDS — <c>additional_tools</c> (harness tool-registration preamble)
/// and <c>agent_message</c> (inter-agent) — are deliberately NOT modeled: a typed record
/// would be write-only ceremony that also re-introduces the 400-on-shape-evolution this
/// converter exists to end (a <c>required</c> field missing, or an unmodeled sibling
/// field silently dropped, on an evolved variant). They ride the
/// <see cref="ResponsesUnknownItem"/> path, which is strictly more byte-faithful (whole
/// <c>Raw</c> element incl. every sibling field, in input order).</para>
/// </remarks>
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
/// <remarks>
/// KNOWN RESIDUAL (field-granular loss): the universal unknown-item passthrough cures
/// unmodeled <c>type</c>s, but a MODELED type like this is bound field-by-field, and STJ
/// silently skips any wire field not modeled here (no <c>JsonUnmappedMemberHandling.Disallow</c>).
/// That is the mechanism that produced the <c>namespace</c> bug (a new field that had to
/// round-trip was dropped, 400ing the echo turn). Every field Copilot currently sends on a
/// <c>function_call</c> IS modeled (corpus replay of 1213 real inbounds is clean), but a
/// FUTURE new field would recur the bug. A full fix (carry unmapped members through the IR
/// tool_use block) is a separate change — this record doesn't round-trip as itself, it
/// becomes an IR <c>tool_use</c>, so <c>[JsonExtensionData]</c> here wouldn't survive to T2.
/// Tracked in docs; if a new field appears, model it here AND re-emit it in T2.
/// </remarks>
internal sealed record ResponsesFunctionCallItem : ResponsesInputItem
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    /// <summary>JSON-encoded arguments STRING (e.g. <c>"{\"command\":\"ls\"}"</c>).</summary>
    public required string Arguments { get; init; }
    public string? Id { get; init; }
    public string? Status { get; init; }
    /// <summary>
    /// The tool's namespace, when it belongs to a NON-default namespace (a gpt-5.6
    /// collaboration/MCP tool registered under a <c>{"type":"namespace",...}</c>
    /// wrapper — e.g. <c>collaboration</c> for <c>list_agents</c>/<c>spawn_agent</c>).
    /// Copilot streams it on the <c>function_call</c> output item and REQUIRES the
    /// client to round-trip it back on echo, else the next turn 400s with
    /// <c>Missing namespace for function_call '&lt;name&gt;'. ... Round-trip the model's
    /// function_call item with its namespace field included.</c> Null for a plain
    /// default-namespace function tool (the field is then absent on the wire). Source
    /// of truth: openai/codex <c>ev_function_call_with_namespace</c> fixture +
    /// vercel/ai SDK (<c>providerMetadata.openai.namespace</c>).
    /// </summary>
    public string? Namespace { get; init; }
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
/// Catch-all for an <c>input[]</c> item whose <c>type</c> the bridge does NOT model
/// (a future gpt-5.6 feature: <c>tool_search_call</c>, <c>compaction</c>,
/// <c>web_search_call</c>, …). Captured WHOLE as an opaque <see cref="JsonElement"/>
/// by <see cref="Converters.ResponsesInputItemListConverter"/> and re-emitted VERBATIM
/// by T2 — so an unmodeled item is carried through losslessly instead of 400'ing the
/// request. This is the universal escape hatch that ends the per-type whack-a-mole.
/// Never constructed by the source generator (it has no <c>[JsonDerivedType]</c>
/// discriminator); the converter builds it for any unknown <c>type</c>.
/// </summary>
internal sealed record ResponsesUnknownItem : ResponsesInputItem
{
    /// <summary>The item's <c>type</c> value (for logging/diagnostics), or "" if absent.</summary>
    public required string Type { get; init; }
    /// <summary>The entire original item object, byte-faithful, re-emitted as-is by T2.</summary>
    public required JsonElement Raw { get; init; }
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

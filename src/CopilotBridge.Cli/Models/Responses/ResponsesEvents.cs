using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Responses;

/// <summary>
/// The Responses SSE events T3 (Responses→IR) parses and T4 (IR→Responses) emits.
/// Grounded in the OpenAI SDK event interfaces
/// (<c>references/openai-sdk-pkg/package/resources/responses/responses.d.ts</c>)
/// and the live event sequence the contract snapshot recorded
/// (<c>response.created → in_progress → output_item.added/done →
/// output_text.delta → function_call_arguments.delta/done → completed</c>;
/// terminal <c>failed</c>/<c>incomplete</c>; NO <c>[DONE]</c>).
/// </summary>
/// <remarks>
/// <para>
/// Only the events the bridge's streaming translation actually needs are modeled,
/// and only the fields it reads — the big nested <c>response</c>/<c>item</c>
/// payloads are held as <see cref="JsonElement"/> (T3 reads a couple of fields
/// off them; reserializing the whole graph through strongly-typed DTOs would
/// bloat the AOT image for no fidelity gain, since these are streaming
/// intermediates, not round-tripped request bodies).
/// </para>
/// <para>
/// These DTOs exist so T3/T4 can read/write event payloads type-safely via the
/// source-gen context. The <c>type</c> discriminator drives the polymorphic
/// dispatch; an event type the bridge doesn't model is handled opaquely by the
/// strategy (forwarded/translated by name) rather than deserialized here.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ResponseCreatedEvent), "response.created")]
[JsonDerivedType(typeof(ResponseInProgressEvent), "response.in_progress")]
[JsonDerivedType(typeof(ResponseOutputItemAddedEvent), "response.output_item.added")]
[JsonDerivedType(typeof(ResponseOutputItemDoneEvent), "response.output_item.done")]
[JsonDerivedType(typeof(ResponseOutputTextDeltaEvent), "response.output_text.delta")]
[JsonDerivedType(typeof(ResponseOutputTextDoneEvent), "response.output_text.done")]
[JsonDerivedType(typeof(ResponseFunctionCallArgumentsDeltaEvent), "response.function_call_arguments.delta")]
[JsonDerivedType(typeof(ResponseFunctionCallArgumentsDoneEvent), "response.function_call_arguments.done")]
[JsonDerivedType(typeof(ResponseReasoningTextDeltaEvent), "response.reasoning_text.delta")]
[JsonDerivedType(typeof(ResponseCompletedEvent), "response.completed")]
[JsonDerivedType(typeof(ResponseFailedEvent), "response.failed")]
[JsonDerivedType(typeof(ResponseIncompleteEvent), "response.incomplete")]
internal abstract record ResponsesStreamEvent
{
    public int? SequenceNumber { get; init; }
}

/// <summary><c>response.created</c> — opens the stream; carries the initial <c>response</c> object.</summary>
internal sealed record ResponseCreatedEvent : ResponsesStreamEvent
{
    public JsonElement? Response { get; init; }
}

/// <summary><c>response.in_progress</c>.</summary>
internal sealed record ResponseInProgressEvent : ResponsesStreamEvent
{
    public JsonElement? Response { get; init; }
}

/// <summary><c>response.output_item.added</c> — a new output item (message or function_call) starts.</summary>
internal sealed record ResponseOutputItemAddedEvent : ResponsesStreamEvent
{
    public int? OutputIndex { get; init; }
    /// <summary>The output item (a message or function_call) — read for id/type/name.</summary>
    public JsonElement? Item { get; init; }
}

/// <summary><c>response.output_item.done</c> — an output item completes.</summary>
internal sealed record ResponseOutputItemDoneEvent : ResponsesStreamEvent
{
    public int? OutputIndex { get; init; }
    public JsonElement? Item { get; init; }
}

/// <summary><c>response.output_text.delta</c> — a text fragment for the assistant message.</summary>
internal sealed record ResponseOutputTextDeltaEvent : ResponsesStreamEvent
{
    public string? ItemId { get; init; }
    public int? OutputIndex { get; init; }
    public int? ContentIndex { get; init; }
    public required string Delta { get; init; }
}

/// <summary><c>response.output_text.done</c> — the full text for an item.</summary>
internal sealed record ResponseOutputTextDoneEvent : ResponsesStreamEvent
{
    public string? ItemId { get; init; }
    public int? OutputIndex { get; init; }
    public string? Text { get; init; }
}

/// <summary><c>response.function_call_arguments.delta</c> — a JSON fragment of a tool call's arguments.</summary>
internal sealed record ResponseFunctionCallArgumentsDeltaEvent : ResponsesStreamEvent
{
    public string? ItemId { get; init; }
    public int? OutputIndex { get; init; }
    public required string Delta { get; init; }
}

/// <summary><c>response.function_call_arguments.done</c> — the complete arguments string + tool name.</summary>
internal sealed record ResponseFunctionCallArgumentsDoneEvent : ResponsesStreamEvent
{
    public string? ItemId { get; init; }
    public int? OutputIndex { get; init; }
    public string? Name { get; init; }
    public required string Arguments { get; init; }
}

/// <summary><c>response.reasoning_text.delta</c> — a reasoning-summary fragment (when reasoning summary is on).</summary>
internal sealed record ResponseReasoningTextDeltaEvent : ResponsesStreamEvent
{
    public string? ItemId { get; init; }
    public int? OutputIndex { get; init; }
    public required string Delta { get; init; }
}

/// <summary><c>response.completed</c> — terminal success. Codex's parser REQUIRES this (no <c>[DONE]</c>).</summary>
internal sealed record ResponseCompletedEvent : ResponsesStreamEvent
{
    /// <summary>The final <c>response</c> object — read for status + usage.</summary>
    public JsonElement? Response { get; init; }
}

/// <summary><c>response.failed</c> — terminal failure.</summary>
internal sealed record ResponseFailedEvent : ResponsesStreamEvent
{
    public JsonElement? Response { get; init; }
}

/// <summary><c>response.incomplete</c> — terminal incomplete (e.g. max tokens).</summary>
internal sealed record ResponseIncompleteEvent : ResponsesStreamEvent
{
    public JsonElement? Response { get; init; }
}

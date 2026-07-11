using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CopilotBridge.Cli.Models.Responses;

namespace CopilotBridge.Cli.Models.Responses.Converters;

/// <summary>
/// Deserializes the Responses <c>input[]</c> array with a UNIVERSAL fallback for
/// unmodeled item types, ending the per-type whack-a-mole that shipped four gpt-5.6
/// tool bugs.
/// </summary>
/// <remarks>
/// <para>The default STJ <c>[JsonPolymorphic]</c> whitelist throws
/// <c>Polymorphism_UnrecognizedTypeDiscriminator</c> (→ HTTP 400, BEFORE T1 runs) on
/// ANY <c>type</c> it doesn't list. But the Responses <c>input[]</c> union is
/// open-ended — gpt-5.6's multi-agent / tool-search / compaction features keep adding
/// item types (<c>agent_message</c>, <c>tool_search_call</c>, <c>compaction</c>, …).</para>
/// <para>This converter reads each item into a <see cref="JsonElement"/>, peeks its
/// <c>type</c>, and: for a KNOWN type, re-deserializes through the source-generated
/// polymorphic <see cref="ResponsesInputItem"/> metadata (byte-for-byte the same
/// binding as before — no behavior change for modeled types); for an UNKNOWN type,
/// captures the whole item as a <see cref="ResponsesUnknownItem"/> (opaque, byte-
/// faithful) so T2 re-emits it verbatim. Array ORDER is preserved (an
/// <c>agent_message</c> sits between messages, not hoisted like additional_tools).</para>
/// <para>AOT-clean: no reflection — the known-type branch uses
/// <c>options.GetTypeInfo(typeof(ResponsesInputItem))</c> (source-gen metadata), the
/// unknown branch only clones a <see cref="JsonElement"/>.</para>
/// </remarks>
internal sealed class ResponsesInputItemListConverter : JsonConverter<IReadOnlyList<ResponsesInputItem>>
{
    // The item `type` discriminators the bridge models. Kept in sync with the
    // [JsonDerivedType] attributes on ResponsesInputItem BY A TEST
    // (KnownTypesMatchesDerivedTypesTests) — not just by hope: if the two drift, a
    // modeled type would silently route to the unknown passthrough. Anything not here
    // → ResponsesUnknownItem (opaque, byte-faithful). Ordinal: the wire uses lowercase
    // snake_case types.
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "message",
        "function_call",
        "function_call_output",
        "reasoning",
        "additional_tools",
    };

    public override IReadOnlyList<ResponsesInputItem> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected array for input[]; got {reader.TokenType}.");

        var typeInfo = (JsonTypeInfo<ResponsesInputItem>)options.GetTypeInfo(typeof(ResponsesInputItem));
        var list = new List<ResponsesInputItem>();

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            // Materialize the item as a JsonElement so we can peek `type` WITHOUT
            // letting the polymorphic binder throw on an unknown discriminator.
            using var doc = JsonDocument.ParseValue(ref reader);
            var element = doc.RootElement;
            var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";

            if (type.Length > 0 && KnownTypes.Contains(type))
            {
                // Known type → bind through the source-generated polymorphic metadata,
                // exactly as the default path would (no behavior change).
                var known = element.Deserialize(typeInfo)
                    ?? throw new JsonException($"input[] item of type '{type}' deserialized to null.");
                list.Add(known);
            }
            else
            {
                // Unknown (or type-less) item → carry it whole, byte-faithful. Clone so
                // it outlives the JsonDocument disposed at the end of this iteration.
                list.Add(new ResponsesUnknownItem { Type = type, Raw = element.Clone() });
            }

            reader.Read();
        }
        return list;
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyList<ResponsesInputItem> value, JsonSerializerOptions options)
    {
        // T2 builds the outbound input[] by hand (ResponsesRequestBuilder); this Write
        // exists only so the type is round-trippable through STJ (e.g. tests). Known
        // items serialize via source-gen metadata; an unknown item writes its raw bytes.
        var typeInfo = (JsonTypeInfo<ResponsesInputItem>)options.GetTypeInfo(typeof(ResponsesInputItem));
        writer.WriteStartArray();
        foreach (var item in value)
        {
            if (item is ResponsesUnknownItem unknown)
                unknown.Raw.WriteTo(writer);
            else
                JsonSerializer.Serialize(writer, item, typeInfo);
        }
        writer.WriteEndArray();
    }
}

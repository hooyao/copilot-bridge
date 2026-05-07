using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Models.Anthropic.Converters;

/// <summary>
/// Normalizes the Anthropic <c>system</c> union (<c>string |
/// Array&lt;TextBlockParam&gt;</c>): a string is wrapped as a single block;
/// output is always an array. Claude Code typically sends array form already
/// (so cache_control breakpoints can attach), but the spec allows the string
/// shorthand.
/// </summary>
internal sealed class TextBlockParamListConverter : JsonConverter<IReadOnlyList<TextBlockParam>>
{
    public override IReadOnlyList<TextBlockParam> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [new TextBlockParam { Text = reader.GetString()! }];
        }
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected string or array for system; got {reader.TokenType}.");
        }

        var typeInfo = (JsonTypeInfo<TextBlockParam>)options.GetTypeInfo(typeof(TextBlockParam));
        var list = new List<TextBlockParam>();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(JsonSerializer.Deserialize(ref reader, typeInfo)!);
            reader.Read();
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<TextBlockParam> value, JsonSerializerOptions options)
    {
        // Serialize via the polymorphic base's TypeInfo so the
        // [JsonDerivedType] discriminator (`"type":"text"`) is emitted —
        // Copilot rejects system blocks missing the type field with HTTP 400.
        var typeInfo = (JsonTypeInfo<ContentBlockParam>)options.GetTypeInfo(typeof(ContentBlockParam));
        writer.WriteStartArray();
        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, (ContentBlockParam)item, typeInfo);
        }
        writer.WriteEndArray();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Models.Anthropic.Converters;

/// <summary>
/// Normalizes the Anthropic <c>messages[].content</c> union (<c>string |
/// Array&lt;ContentBlockParam&gt;</c>): a string input is wrapped as a single
/// <see cref="TextBlockParam"/>; output is always written as an array.
/// </summary>
internal sealed class ContentBlockParamListConverter : JsonConverter<IReadOnlyList<ContentBlockParam>>
{
    public override IReadOnlyList<ContentBlockParam> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [new TextBlockParam { Text = reader.GetString()! }];
        }
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected string or array for content; got {reader.TokenType}.");
        }

        var typeInfo = (JsonTypeInfo<ContentBlockParam>)options.GetTypeInfo(typeof(ContentBlockParam));
        var list = new List<ContentBlockParam>();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(JsonSerializer.Deserialize(ref reader, typeInfo)!);
            reader.Read();
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ContentBlockParam> value, JsonSerializerOptions options)
    {
        var typeInfo = (JsonTypeInfo<ContentBlockParam>)options.GetTypeInfo(typeof(ContentBlockParam));
        writer.WriteStartArray();
        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, typeInfo);
        }
        writer.WriteEndArray();
    }
}

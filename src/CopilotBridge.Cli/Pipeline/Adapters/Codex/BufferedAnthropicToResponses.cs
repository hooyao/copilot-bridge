using System.Text.Json;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Cli.Pipeline.Adapters.Codex;

/// <summary>Buffered T4: Anthropic-shaped response IR to a Responses object.</summary>
internal static class BufferedAnthropicToResponses
{
    private const string GrammarMarker = "bridge_input_is_grammar_text";
    private const string NamespaceMarker = "bridge_tool_namespace";

    public static byte[]? TryTranslate(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadString(root, "type") != "message")
                return null;

            if (!root.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                throw InvalidIr();

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", ReadString(root, "id") ?? "resp_bridge");
                writer.WriteString("object", "response");
                writer.WriteString("status", ReadString(root, "stop_reason") == "max_tokens"
                    ? "incomplete"
                    : "completed");
                writer.WriteString("model", ReadString(root, "model") ?? "unknown");
                writer.WritePropertyName("output");
                writer.WriteStartArray();

                var outputIndex = 0;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object)
                        continue;

                    switch (ReadString(block, "type"))
                    {
                        case "text":
                            WriteTextItem(writer, block, outputIndex++);
                            break;
                        case "tool_use":
                            WriteToolItem(writer, block, outputIndex++);
                            break;
                    }
                }

                writer.WriteEndArray();
                WriteUsage(writer, root);
                writer.WriteEndObject();
            }
            return buffer.ToArray();
        }
    }

    private static void WriteTextItem(Utf8JsonWriter writer, JsonElement block, int index)
    {
        var text = ReadString(block, "text") ?? throw InvalidIr();
        writer.WriteStartObject();
        writer.WriteString("type", "message");
        writer.WriteString("id", $"item_{index}");
        writer.WriteString("role", "assistant");
        writer.WriteString("status", "completed");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "output_text");
        writer.WriteString("text", text);
        writer.WritePropertyName("annotations");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteToolItem(Utf8JsonWriter writer, JsonElement block, int index)
    {
        var callId = ReadRequiredString(block, "id");
        var name = ReadRequiredString(block, "name");
        if (!block.TryGetProperty("input", out var input))
            throw InvalidIr();

        var custom = block.TryGetProperty(GrammarMarker, out var grammar)
            && grammar.ValueKind == JsonValueKind.True;

        writer.WriteStartObject();
        writer.WriteString("type", custom ? "custom_tool_call" : "function_call");
        writer.WriteString("id", $"item_{index}");
        writer.WriteString("call_id", callId);
        if (ReadString(block, NamespaceMarker) is { Length: > 0 } toolNamespace)
            writer.WriteString("namespace", toolNamespace);
        writer.WriteString("name", name);
        if (custom)
        {
            if (input.ValueKind != JsonValueKind.String)
                throw InvalidIr();
            writer.WriteString("input", input.GetString());
        }
        else
        {
            if (input.ValueKind != JsonValueKind.Object)
                throw InvalidIr();
            writer.WriteString("arguments", input.GetRawText());
        }
        writer.WriteString("status", "completed");
        writer.WriteEndObject();
    }

    private static void WriteUsage(Utf8JsonWriter writer, JsonElement root)
    {
        var input = 0L;
        var output = 0L;
        var cached = 0L;
        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            input = ReadNonNegativeInt64(usage, "input_tokens");
            output = ReadNonNegativeInt64(usage, "output_tokens");
            cached = ReadNonNegativeInt64(usage, "cache_read_input_tokens");
        }

        writer.WritePropertyName("usage");
        writer.WriteStartObject();
        writer.WriteNumber("input_tokens", input);
        writer.WritePropertyName("input_tokens_details");
        writer.WriteStartObject();
        writer.WriteNumber("cached_tokens", cached);
        writer.WriteEndObject();
        writer.WriteNumber("output_tokens", output);
        writer.WritePropertyName("output_tokens_details");
        writer.WriteStartObject();
        writer.WriteNumber("reasoning_tokens", 0);
        writer.WriteEndObject();
        writer.WriteNumber("total_tokens", input + output);
        writer.WriteEndObject();
    }

    private static string ReadRequiredString(JsonElement element, string property) =>
        ReadString(element, property) is { Length: > 0 } value ? value : throw InvalidIr();

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadNonNegativeInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number)
            ? Math.Max(0, number)
            : 0;

    private static UpstreamResponseFailedException InvalidIr() =>
        new("invalid_buffered_ir");
}

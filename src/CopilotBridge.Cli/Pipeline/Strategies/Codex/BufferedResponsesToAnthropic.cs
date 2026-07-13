using System.Text.Json;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// Buffered T3: converts a successful Responses object into the Anthropic-shaped
/// response IR before response stages run. Non-Responses bodies are left
/// untouched; an identified Responses failure becomes a bounded typed fault.
/// </summary>
internal static class BufferedResponsesToAnthropic
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
                || !root.TryGetProperty("object", out var objectType)
                || objectType.ValueKind != JsonValueKind.String
                || objectType.GetString() != "response")
                return null;

            if (!root.TryGetProperty("status", out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String)
                return null;

            var status = statusElement.GetString();
            if (status == "failed")
                throw new UpstreamResponseFailedException(ReadFailureCode(root));
            if (status is not ("completed" or "incomplete"))
                return null;

            if (!root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
                throw InvalidResponse();

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", ReadString(root, "id") ?? "msg_bridge");
                writer.WriteString("type", "message");
                writer.WriteString("role", "assistant");
                writer.WriteString("model", ReadString(root, "model") ?? "unknown");
                writer.WritePropertyName("content");
                writer.WriteStartArray();

                var hasToolUse = false;
                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    switch (ReadString(item, "type"))
                    {
                        case "message":
                            WriteMessageBlocks(writer, item);
                            break;

                        case "function_call":
                            WriteFunctionCall(writer, item);
                            hasToolUse = true;
                            break;

                        case "custom_tool_call":
                            WriteCustomToolCall(writer, item);
                            hasToolUse = true;
                            break;
                    }
                }

                writer.WriteEndArray();
                // Incomplete is an upstream terminal fact and takes precedence over
                // an output item that happened to look executable before the limit.
                writer.WriteString("stop_reason", statusElement.GetString() == "incomplete"
                    ? "max_tokens"
                    : hasToolUse ? "tool_use" : "end_turn");
                writer.WriteNull("stop_sequence");
                WriteUsage(writer, root);
                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }
    }

    private static void WriteMessageBlocks(Utf8JsonWriter writer, JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || ReadString(part, "type") != "output_text")
                continue;

            var text = ReadString(part, "text") ?? throw InvalidResponse();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }
    }

    private static void WriteFunctionCall(Utf8JsonWriter writer, JsonElement item)
    {
        var callId = ReadRequiredString(item, "call_id");
        var name = ReadRequiredString(item, "name");
        var arguments = ReadRequiredString(item, "arguments");

        JsonDocument argumentsDoc;
        try
        {
            argumentsDoc = JsonDocument.Parse(arguments);
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }

        using (argumentsDoc)
        {
            if (argumentsDoc.RootElement.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();

            writer.WriteStartObject();
            writer.WriteString("type", "tool_use");
            writer.WriteString("id", callId);
            writer.WriteString("name", name);
            writer.WritePropertyName("input");
            argumentsDoc.RootElement.WriteTo(writer);
            WriteNamespace(writer, item);
            writer.WriteEndObject();
        }
    }

    private static void WriteCustomToolCall(Utf8JsonWriter writer, JsonElement item)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "tool_use");
        writer.WriteString("id", ReadRequiredString(item, "call_id"));
        writer.WriteString("name", ReadRequiredString(item, "name"));
        writer.WriteString("input", ReadRequiredString(item, "input"));
        writer.WriteBoolean(GrammarMarker, true);
        WriteNamespace(writer, item);
        writer.WriteEndObject();
    }

    private static void WriteNamespace(Utf8JsonWriter writer, JsonElement item)
    {
        if (ReadString(item, "namespace") is { Length: > 0 } toolNamespace)
            writer.WriteString(NamespaceMarker, toolNamespace);
    }

    private static void WriteUsage(Utf8JsonWriter writer, JsonElement root)
    {
        var inputTokens = 0;
        var outputTokens = 0;
        var cacheReadTokens = 0;
        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = ReadNonNegativeInt32(usage, "input_tokens");
            outputTokens = ReadNonNegativeInt32(usage, "output_tokens");
            if (usage.TryGetProperty("input_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
                cacheReadTokens = ReadNonNegativeInt32(details, "cached_tokens");
        }

        writer.WritePropertyName("usage");
        writer.WriteStartObject();
        writer.WriteNumber("input_tokens", inputTokens);
        writer.WriteNumber("output_tokens", outputTokens);
        writer.WriteNumber("cache_read_input_tokens", cacheReadTokens);
        writer.WriteEndObject();
    }

    private static string ReadRequiredString(JsonElement element, string property) =>
        ReadString(element, property) is { Length: > 0 } value ? value : throw InvalidResponse();

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadNonNegativeInt32(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt64(out var number))
            return 0;
        return (int)Math.Clamp(number, 0, int.MaxValue);
    }

    private static string? ReadFailureCode(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object)
            return ReadString(error, "code");
        return null;
    }

    private static UpstreamResponseFailedException InvalidResponse() =>
        new("invalid_buffered_response");
}

using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// Strict parser for Claude Code 2.1.221's count-tokens body. Native Anthropic
/// counting never uses it (raw passthrough); it protects the cross-protocol path
/// from silently dropping a newly introduced token-bearing top-level field.
/// </summary>
internal static class ResponsesCountRequest
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "model", "messages", "tools", "thinking", "output_config",
    };

    private static readonly string[] MessageFields = ["role", "content"];
    private static readonly string[] ToolFields =
    [
        "name", "type", "input_schema", "description", "cache_control",
        "defer_loading", "strict",
    ];
    private static readonly string[] CacheControlFields = ["type", "ttl"];
    private static readonly string[] InputSchemaFields =
        ["type", "properties", "required"];

    public static bool TryParse(
        ReadOnlySpan<byte> body,
        out MessagesRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "count_tokens body must be a JSON object";
                return false;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!Supported.Contains(property.Name))
                {
                    error = $"unsupported cross-routed count_tokens field '{property.Name}'";
                    return false;
                }
            }

            if (!ValidateObjectFields(
                    doc.RootElement, "output_config", ["effort"], out error)
                || !ValidateObjectFields(
                    doc.RootElement, "thinking",
                    ["type", "budget_tokens", "display"], out error)
                || !ValidateMessages(doc.RootElement, out error)
                || !ValidateTools(doc.RootElement, out error))
                return false;

            request = JsonSerializer.Deserialize(
                body, JsonContext.Default.MessagesRequest);
            if (request is null
                || string.IsNullOrWhiteSpace(request.Model)
                || request.Messages is null)
            {
                error = "count_tokens body requires model and messages";
                request = null;
                return false;
            }

            // These are generation-only and must never be synthesized for a
            // count-only body, even though MessagesRequest has value defaults.
            request = request with
            {
                MaxTokens = 0,
                System = null,
                ToolChoice = null,
                ContextManagement = null,
                CacheControl = null,
                Metadata = null,
                StopSequences = null,
                Stream = null,
                Temperature = null,
                AnthropicBeta = null,
                ProviderExtensions = null,
            };
            return true;
        }
        catch (JsonException ex)
        {
            error = "invalid count_tokens body: " + ex.Message;
            return false;
        }
    }

    private static bool ValidateObjectFields(
        JsonElement root,
        string propertyName,
        string[] supported,
        out string? error)
    {
        error = null;
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"cross-routed count_tokens field '{propertyName}' must be an object";
            return false;
        }
        foreach (var property in value.EnumerateObject())
        {
            if (supported.Contains(property.Name)) continue;
            error = $"unsupported cross-routed count_tokens field "
                + $"'{propertyName}.{property.Name}'";
            return false;
        }
        return true;
    }

    private static bool ValidateMessages(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
            return true; // The typed parser supplies the required/type error.

        var messageIndex = 0;
        foreach (var message in messages.EnumerateArray())
        {
            var path = $"messages[{messageIndex}]";
            if (!ValidateFields(message, path, MessageFields, out error)) return false;
            if (message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                var blockIndex = 0;
                foreach (var block in content.EnumerateArray())
                {
                    if (!ValidateContentBlock(
                            block, $"{path}.content[{blockIndex}]", out error))
                        return false;
                    blockIndex++;
                }
            }
            messageIndex++;
        }
        return true;
    }

    private static bool ValidateContentBlock(
        JsonElement block, string path, out string? error)
    {
        error = null;
        if (block.ValueKind != JsonValueKind.Object)
            return true; // The polymorphic typed parser supplies the shape error.
        if (!block.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
            return true;

        var fields = typeElement.GetString() switch
        {
            "text" => new[] { "type", "text", "cache_control" },
            "image" => new[] { "type", "source", "cache_control" },
            // Responses T2 has no document mapping. Failing explicitly avoids
            // counting a request after silently deleting the document content.
            "document" => null,
            // input and content are opaque application JSON. Validate their
            // containers, never their user-defined descendants.
            "tool_use" => new[]
                { "type", "id", "name", "input", "cache_control" },
            "tool_result" => new[]
                { "type", "tool_use_id", "content", "is_error", "cache_control" },
            "thinking" => new[] { "type", "thinking", "signature" },
            "redacted_thinking" => new[] { "type", "data" },
            _ => null,
        };
        if (fields is null)
        {
            if (typeElement.GetString() == "document")
            {
                error = $"unsupported cross-routed count_tokens block '{path}.type=document'";
                return false;
            }
            return true; // Typed polymorphism rejects unknown types.
        }
        if (!ValidateFields(block, path, fields, out error)) return false;

        if (block.TryGetProperty("cache_control", out var cacheControl)
            && cacheControl.ValueKind != JsonValueKind.Null
            && !ValidateFields(
                cacheControl, path + ".cache_control", CacheControlFields, out error))
            return false;

        if (block.TryGetProperty("source", out var source)
            && source.ValueKind == JsonValueKind.Object
            && !ValidateSource(source, path + ".source", out error))
            return false;
        if (typeElement.GetString() == "tool_result"
            && block.TryGetProperty("content", out var resultContent)
            && !ValidateToolResultContent(
                resultContent, path + ".content", out error))
            return false;
        return true;
    }

    private static bool ValidateToolResultContent(
        JsonElement content, string path, out string? error)
    {
        error = null;
        if (content.ValueKind != JsonValueKind.Array) return true;
        var index = 0;
        foreach (var block in content.EnumerateArray())
        {
            // T2 extracts only `text` from a text result block. Other result
            // block types are emitted as their complete compact JSON, so their
            // application-defined fields remain token-bearing and opaque.
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && !ValidateFields(
                    block, $"{path}[{index}]", ["type", "text"], out error))
                return false;
            index++;
        }
        return true;
    }

    private static bool ValidateTools(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
            return true;
        var index = 0;
        foreach (var tool in tools.EnumerateArray())
        {
            var path = $"tools[{index}]";
            if (!ValidateFields(tool, path, ToolFields, out error)) return false;
            if (tool.TryGetProperty("cache_control", out var cacheControl)
                && cacheControl.ValueKind != JsonValueKind.Null
                && !ValidateFields(
                    cacheControl, path + ".cache_control", CacheControlFields, out error))
                return false;
            if (tool.TryGetProperty("input_schema", out var inputSchema)
                && inputSchema.ValueKind != JsonValueKind.Null
                && !ValidateFields(
                    inputSchema, path + ".input_schema", InputSchemaFields, out error))
                return false;
            // JSON-Schema descendants under properties are intentionally opaque.
            index++;
        }
        return true;
    }

    private static bool ValidateSource(
        JsonElement source, string path, out string? error)
    {
        error = null;
        if (!source.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
            return true;
        var fields = typeElement.GetString() switch
        {
            "base64" => new[] { "type", "data", "media_type" },
            "text" => new[] { "type", "data", "media_type" },
            "content" => new[] { "type", "content" },
            "url" => new[] { "type", "url" },
            // ImageToDataUrl has no file-id mapping. Reject rather than count an
            // empty image URL. Document blocks are rejected before this point.
            "file" => null,
            _ => null,
        };
        if (fields is null && typeElement.GetString() == "file")
        {
            error = $"unsupported cross-routed count_tokens source '{path}.type=file'";
            return false;
        }
        return fields is null || ValidateFields(source, path, fields, out error);
    }

    private static bool ValidateFields(
        JsonElement value, string path, string[] supported, out string? error)
    {
        error = null;
        if (value.ValueKind != JsonValueKind.Object) return true;
        foreach (var property in value.EnumerateObject())
        {
            if (supported.Contains(property.Name)) continue;
            error = $"unsupported cross-routed count_tokens field '{path}.{property.Name}'";
            return false;
        }
        return true;
    }
}

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
                    ["type", "budget_tokens", "display"], out error))
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
}

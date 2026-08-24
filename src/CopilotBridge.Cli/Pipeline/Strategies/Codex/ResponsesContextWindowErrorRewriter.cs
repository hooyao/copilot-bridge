using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Errors;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>Narrow, bounded rewrite for Copilot's confirmed Responses context
/// rejection at the Claude edge.</summary>
internal static class ResponsesContextWindowErrorRewriter
{
    internal const int MaxInspectionBytes = ResponsesContextWindowErrorClassifier.MaxInspectionBytes;

    public static bool TryRewrite(
        string path,
        BackendVendor vendor,
        int status,
        byte[] originalBody,
        IDictionary<string, string> headers,
        out byte[] body)
    {
        body = originalBody;
        if (!path.StartsWith("/cc/", StringComparison.OrdinalIgnoreCase)
            || !ResponsesContextWindowErrorClassifier.IsConfirmed(vendor, status, originalBody))
            return false;

        var envelope = new ErrorResponse
        {
            Error = new ErrorBody
            {
                Type = "invalid_request_error",
                Message = "prompt is too long for the selected model",
            },
        };
        body = JsonSerializer.SerializeToUtf8Bytes(
            envelope, JsonContext.Default.ErrorResponse);
        headers["Content-Type"] = "application/json";
        headers["Content-Length"] = body.Length.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}

internal static class ResponsesContextWindowErrorClassifier
{
    internal const int MaxInspectionBytes = 64 * 1024;
    private const string ConfirmedMessage =
        "Your input exceeds the context window of this model. Please adjust your input and try again.";

    public static bool IsConfirmed(
        BackendVendor vendor,
        int status,
        byte[] body)
    {
        if (status != 400
            || vendor != BackendVendor.CopilotResponses
            || body.Length == 0
            || body.Length > MaxInspectionBytes)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(
                    code.GetString(), "invalid_request_body", StringComparison.Ordinal)
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && string.Equals(
                    message.GetString(), ConfirmedMessage, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

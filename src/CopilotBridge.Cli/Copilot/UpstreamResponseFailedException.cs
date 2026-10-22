using System.Text.Json;

namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// An upstream Responses stream reached an explicit <c>response.failed</c>
/// terminal. Carries a bounded machine code and only reviewed, exact-match
/// diagnostics. Arbitrary generated upstream text never crosses this boundary.
/// </summary>
internal sealed class UpstreamResponseFailedException : Exception
{
    private const int MaxCodeLength = 64;
    internal const string GenericClientMessage = "the upstream model backend failed mid-stream";
    internal const string EncryptedContentMessage = "Encrypted function output content could not be decrypted or decoded.";

    public string Code { get; }
    public string ClientMessage { get; }

    public UpstreamResponseFailedException(string? code, string? diagnostic = null)
        : base($"upstream Responses stream failed (code={NormalizeCode(code)})"
            + (diagnostic == EncryptedContentMessage ? $": {EncryptedContentMessage}" : ""))
    {
        Code = NormalizeCode(code);
        ClientMessage = diagnostic == EncryptedContentMessage ? EncryptedContentMessage : GenericClientMessage;
    }

    internal static UpstreamResponseFailedException FromEvent(JsonElement root)
    {
        var error = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
                error = nested;
            else if (root.TryGetProperty("error", out var top) && top.ValueKind == JsonValueKind.Object)
                error = top;
        }
        return new UpstreamResponseFailedException(ReadString(error, "code"), ReadString(error, "message"));
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
            return "unknown";

        foreach (var ch in code)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                return "unknown";
        }

        return code;
    }
}

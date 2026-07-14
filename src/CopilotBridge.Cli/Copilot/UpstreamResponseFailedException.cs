namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// An upstream Responses stream reached an explicit <c>response.failed</c>
/// terminal. Carries only a bounded machine-readable code; upstream messages can
/// contain generated text and therefore must not cross the client or log boundary.
/// </summary>
internal sealed class UpstreamResponseFailedException : Exception
{
    private const int MaxCodeLength = 64;

    public string Code { get; }

    public UpstreamResponseFailedException(string? code)
        : base($"upstream Responses stream failed (code={NormalizeCode(code)})")
    {
        Code = NormalizeCode(code);
    }

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

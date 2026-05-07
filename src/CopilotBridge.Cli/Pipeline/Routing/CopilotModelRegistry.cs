namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// M1 implementation of <see cref="IModelRegistry"/>. Resolves a client-supplied
/// model id to a Copilot backend route, applying:
/// <list type="number">
///   <item>Name normalization: <c>claude-sonnet-4-5-20250929</c> →
///         <c>claude-sonnet-4.5</c> (research §3.7).</item>
///   <item>Alias table: graceful degradation when a client requests a model
///         the backend doesn't have yet (<c>claude-opus-4.8</c> →
///         <c>claude-opus-4.7</c>). Empty for M1; populated as Claude Code
///         starts shipping new defaults ahead of Copilot.</item>
///   <item>Vendor dispatch by prefix: M1 only handles <c>claude-*</c> (routed
///         to <see cref="BackendVendor.CopilotAnthropic"/>); GPT and Gemini
///         routes land in M3.</item>
/// </list>
/// Future enhancement: validate the resolved id against Copilot's live
/// <c>/models</c> response (filter to <c>/v1/messages</c>-supporting). For
/// now, trust the prefix — Copilot itself returns 400 if the model is
/// unrecognized, and that error surfaces through the strategy.
/// </summary>
internal sealed class CopilotModelRegistry : IModelRegistry
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Empty for M1. Populate as needed:
        // ["claude-opus-4.8"] = "claude-opus-4.7",
    };

    public RouteTarget? Resolve(string requestedModelId)
    {
        var normalized = Normalize(requestedModelId);
        var resolved = Aliases.TryGetValue(normalized, out var alias) ? alias : normalized;

        if (resolved.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteTarget(BackendVendor.CopilotAnthropic, "/v1/messages", resolved);
        }
        // M3: gpt-* → CopilotOpenAi /chat/completions; gemini-* → same.
        return null;
    }

    /// <summary>
    /// Convert versioned dash form (<c>claude-sonnet-4-5-20250929</c>) to
    /// dotted form (<c>claude-sonnet-4.5</c>) Copilot expects. Inputs that
    /// don't match the versioned-with-date pattern pass through unchanged.
    /// </summary>
    public static string Normalize(string modelId)
    {
        var parts = modelId.Split('-');
        // Pattern: claude-{family}-{major}-{minor}-{YYYYMMDD}
        if (parts.Length >= 5
            && parts[^1].Length == 8
            && IsAllDigits(parts[^1]))
        {
            return string.Join('-', parts[..^2]) + "." + parts[^2];
        }
        return modelId;
    }

    private static bool IsAllDigits(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i])) return false;
        }
        return true;
    }
}

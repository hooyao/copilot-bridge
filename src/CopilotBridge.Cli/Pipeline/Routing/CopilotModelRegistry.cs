namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Implementation of <see cref="IModelRegistry"/>. Owns three responsibilities:
/// <list type="number">
///   <item>Name normalization: <c>claude-sonnet-4-5-20250929</c> →
///         <c>claude-sonnet-4.5</c> (research §3.7).</item>
///   <item>Alias table for graceful degradation when a client requests a
///         model the backend doesn't have yet.</item>
///   <item>Vendor + endpoint dispatch by prefix.</item>
///   <item><b>Per-model effort-routing capability</b> — delegated to
///         <see cref="CopilotModelCatalog"/>, which derives the routing
///         decision from Copilot's live <c>/models</c> response. Nothing
///         is hardcoded; new models / new variants are picked up at the
///         next bridge restart.</item>
/// </list>
/// </summary>
internal sealed class CopilotModelRegistry : IModelRegistry
{
    private readonly CopilotModelCatalog _catalog;

    public CopilotModelRegistry(CopilotModelCatalog catalog)
    {
        _catalog = catalog;
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Empty by default. Populate as needed:
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
        // GPT / o-series / Gemini all served by Copilot via the OpenAI shape.
        // The actual backend strategy isn't implemented until M3, but we
        // recognize the prefix here so RoutesValidator at startup can confirm
        // a config rule's referenced model is at least known. Requests will
        // fail at the strategy registry (no handler) until M3 lands — that's
        // a clear runtime error, not silent breakage.
        if (resolved.StartsWith("gpt-",  StringComparison.OrdinalIgnoreCase)
         || resolved.StartsWith("o3-",   StringComparison.OrdinalIgnoreCase)
         || resolved.StartsWith("o4-",   StringComparison.OrdinalIgnoreCase)
         || resolved.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteTarget(BackendVendor.CopilotOpenAi, "/chat/completions", resolved);
        }
        return null;
    }

    public (string Model, bool StripEffort) ApplyEffortRouting(string normalizedModelId, string? effort)
    {
        if (effort is null)
        {
            // Client didn't send the field; nothing to strip, no variant
            // selection possible. Pass through unchanged.
            return (normalizedModelId, false);
        }
        var decision = _catalog.DecideEffortRouting(normalizedModelId, effort);
        return (decision.Model, decision.StripEffort);
    }

    /// <summary>
    /// Canonicalize a model id to dotted-version form regardless of date
    /// suffix or trailing variant tags:
    /// <code>
    /// claude-opus-4-7                  → claude-opus-4.7
    /// claude-opus-4-7-20251015         → claude-opus-4.7      (date suffix stripped)
    /// claude-opus-4-7-high             → claude-opus-4.7-high
    /// claude-opus-4-7-1m-internal      → claude-opus-4.7-1m-internal
    /// claude-sonnet-4-5-20250929       → claude-sonnet-4.5
    /// claude-haiku-4.5                 → claude-haiku-4.5     (already canonical)
    /// </code>
    /// Strategy: (1) drop a trailing 8-digit date suffix, (2) merge the first
    /// consecutive <c>digit-digit</c> pair into <c>digit.digit</c>.
    /// </summary>
    public static string Normalize(string modelId)
    {
        var parts = new List<string>(modelId.Split('-'));

        // Strip trailing YYYYMMDD date.
        if (parts.Count >= 2 && parts[^1].Length == 8 && IsAllDigits(parts[^1]))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        // Merge first consecutive numeric pair into "major.minor".
        for (var i = 0; i < parts.Count - 1; i++)
        {
            if (IsAllDigits(parts[i]) && IsAllDigits(parts[i + 1]))
            {
                parts[i] = parts[i] + "." + parts[i + 1];
                parts.RemoveAt(i + 1);
                break;
            }
        }

        return string.Join('-', parts);
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

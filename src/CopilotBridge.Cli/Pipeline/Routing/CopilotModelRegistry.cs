namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// M1 implementation of <see cref="IModelRegistry"/>. Owns three responsibilities:
/// <list type="number">
///   <item>Name normalization: <c>claude-sonnet-4-5-20250929</c> →
///         <c>claude-sonnet-4.5</c> (research §3.7).</item>
///   <item>Alias table for graceful degradation when a client requests a
///         model the backend doesn't have yet.</item>
///   <item>Vendor + endpoint dispatch by prefix.</item>
///   <item><b>Per-model effort-routing capability</b> — the
///         <see cref="EffortAware"/> table declares which models interpret
///         <c>output_config.effort</c> as a routing key (selecting a sized
///         variant). All other models are treated as "do not pass effort
///         through" — the field is stripped before the request reaches
///         Copilot. This collapses what was previously a
///         per-(model,effort) row in <c>appsettings.json</c> into one
///         entry per effort-aware model.</item>
/// </list>
/// Future enhancement: validate the resolved id against Copilot's live
/// <c>/models</c> response (filter to <c>/v1/messages</c>-supporting), and
/// merge the live capability data (supported endpoints, effort levels)
/// into <see cref="EffortAware"/> at startup. See <c>docs/pipeline-design.md</c> §7.3.
/// </summary>
internal sealed class CopilotModelRegistry : IModelRegistry
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Empty for M1. Populate as needed:
        // ["claude-opus-4.8"] = "claude-opus-4.7",
    };

    /// <summary>
    /// Models whose inbound <c>output_config.effort</c> selects a sized
    /// variant. The dictionary is keyed by the canonical (normalized)
    /// model id; the value maps each accepted effort level to a variant
    /// suffix. An empty-string suffix means "the base model itself
    /// handles this effort level" (no model rewrite, but effort is still
    /// stripped because Copilot rejects the field on the wire).
    /// <para/>
    /// Variant-tagged ids (<c>claude-opus-4.7-high</c>, <c>-xhigh</c>, …)
    /// intentionally have <b>no entry</b> — they have a fixed effort
    /// baked in and reject the field; the registry's default behavior
    /// for unregistered models (strip effort, keep model) is correct for
    /// them.
    /// <para/>
    /// New effort-aware models are added here, one row per model. There
    /// is no per-effort row — that's what the inner dictionary is for.
    /// </summary>
    private static readonly Dictionary<string, ModelCapability> EffortAware = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4.7"] = new(EffortToSuffix: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max"]    = "-xhigh",  // no -max variant; xhigh is the closest sized variant
            ["xhigh"]  = "-xhigh",
            ["high"]   = "-high",
            ["medium"] = "",        // base model is implicitly medium-effort
            ["low"]    = "",        // no -low variant; clamp to base
        }),
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
        if (!EffortAware.TryGetValue(normalizedModelId, out var cap))
        {
            // Model is not effort-aware (or not yet registered). Safe
            // default: drop the field. Most Copilot models reject it
            // outright; the few that accept it are listed in EffortAware.
            return (normalizedModelId, true);
        }
        // Known effort-aware model. Map effort → suffix; an unknown effort
        // level for this model clamps to the base ("" suffix).
        var suffix = cap.EffortToSuffix.GetValueOrDefault(effort, "");
        return (normalizedModelId + suffix, true);
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

/// <summary>
/// Per-model effort-routing capability. For now this only carries the
/// effort→suffix map; in M3 it will grow fields for supported endpoints
/// (<c>/chat/completions</c> vs <c>/responses</c>) and supported thinking
/// shapes — populated from Copilot's live <c>/models</c> response when the
/// bridge starts. See <c>docs/pipeline-design.md</c> §7.3.
/// </summary>
internal sealed record ModelCapability(IReadOnlyDictionary<string, string> EffortToSuffix);

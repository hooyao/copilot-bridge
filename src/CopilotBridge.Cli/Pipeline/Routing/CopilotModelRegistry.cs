namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Implementation of <see cref="IModelRegistry"/>: pure name normalization +
/// prefix-based vendor/endpoint dispatch. Per-model capability data lives in
/// <see cref="ModelProfileCatalog"/>, not here — the registry only answers
/// "where does this id go on the wire" (always <c>/v1/messages</c> for
/// <c>claude-*</c>, <c>/chat/completions</c> for the rest).
/// </summary>
internal sealed class CopilotModelRegistry : IModelRegistry
{
    /// <summary>
    /// The Codex/Responses model ids — these resolve to the native
    /// <c>/responses</c> backend (<see cref="BackendVendor.CopilotResponses"/>),
    /// not the legacy <c>/chat/completions</c>. The set is the live-confirmed
    /// Responses-capable models (<c>docs/codex-protocol-research.md</c> §2.1 /
    /// the change-2 contract snapshot), matched on the normalized id. Membership
    /// is an explicit list, never a family-name prefix — sibling gpt ids that are
    /// NOT in this set keep the existing <c>CopilotOpenAi</c> routing.
    /// </summary>
    private static readonly HashSet<string> ResponsesModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.3-codex",
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-5.5",
        "gpt-5-mini",
        // mai-code-1-flash-internal was RETIRED by Copilot (2026 reconciliation:
        // 400 "not available for integrator"); the live Responses id is now
        // mai-code-1-flash-picker (200 — ResponsesProbe.MaiCode_LivenessProbe).
        "mai-code-1-flash-picker",
    };

    public RouteTarget? Resolve(string requestedModelId)
    {
        var normalized = Normalize(requestedModelId);

        if (normalized.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteTarget(BackendVendor.CopilotAnthropic, "/v1/messages", normalized);
        }

        // Codex / Responses-native models → Copilot's native /responses endpoint.
        // This is the backend the Codex CLI speaks (wire_api=responses); the
        // CopilotResponsesStrategy + T1–T4 translators serve it through the shared
        // Anthropic-shape IR. Matched on the explicit id set (snapshot-derived),
        // so a non-Codex gpt id still falls through to the OpenAI-chat branch.
        if (ResponsesModelIds.Contains(normalized))
        {
            return new RouteTarget(BackendVendor.CopilotResponses, "/responses", normalized);
        }

        // GPT / o-series / Gemini all served by Copilot via the OpenAI Chat shape.
        // The actual backend strategy isn't implemented (no /chat/completions
        // strategy ships), so these recognize the prefix but fail at the strategy
        // registry with a clear "no handler" error rather than silently falling
        // through.
        if (normalized.StartsWith("gpt-",  StringComparison.OrdinalIgnoreCase)
         || normalized.StartsWith("o3-",   StringComparison.OrdinalIgnoreCase)
         || normalized.StartsWith("o4-",   StringComparison.OrdinalIgnoreCase)
         || normalized.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteTarget(BackendVendor.CopilotOpenAi, "/chat/completions", normalized);
        }
        return null;
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

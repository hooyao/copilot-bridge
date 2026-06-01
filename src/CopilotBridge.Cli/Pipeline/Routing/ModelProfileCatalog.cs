namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Hand-curated catalog of <see cref="ModelProfile"/>s — one per Copilot
/// Anthropic model the bridge knows how to talk to. This is the bridge's
/// baseline understanding of Copilot's API surface; see <see cref="ModelProfile"/>
/// for why it is hand-curated from playground probes rather than loaded from
/// Copilot's <c>/models</c> endpoint.
/// </summary>
/// <remarks>
/// <para>Lookup is by canonical id (post-<see cref="CopilotModelRegistry.Normalize"/>).
/// A miss is a hard, surfaced error (<see cref="UnknownModelException"/>) — the
/// bridge never guesses a model's wire shape, because guessing wrong produces a
/// silent 400 from Copilot that the user cannot diagnose.</para>
/// <para>Every value below is sourced from a successful or rejected probe
/// recorded in <c>tests/CopilotBridge.Playground/ModelProfileProbe.cs</c>.
/// When Copilot adds or changes a model, re-run that probe and reconcile —
/// don't guess from family names.</para>
/// </remarks>
internal sealed class ModelProfileCatalog
{
    private readonly Dictionary<string, ModelProfile> _byId;

    public ModelProfileCatalog()
    {
        _byId = BuildDefault().ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Test-only: build a catalog from an explicit profile set.</summary>
    internal ModelProfileCatalog(IEnumerable<ModelProfile> profiles)
    {
        _byId = profiles.ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Profile for <paramref name="canonicalId"/>, or null if unknown.</summary>
    public ModelProfile? Get(string canonicalId) =>
        _byId.TryGetValue(canonicalId, out var p) ? p : null;

    /// <summary>All known canonical ids, sorted — used in the unknown-model error body.</summary>
    public IReadOnlyList<string> KnownIds
    {
        get
        {
            var ids = new List<string>(_byId.Keys);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
    }

    public int Count => _byId.Count;

    /// <summary>
    /// The baseline profile set. Every field is grounded in a probe in
    /// <c>ModelProfileProbe.cs</c>; the cited probe lines below let a reader
    /// re-verify without re-running the matrix.
    /// </summary>
    /// <remarks>
    /// Cross-cutting facts learned from probing all 11 models:
    /// <list type="bullet">
    ///   <item>No model accepts <c>output_config.effort = "max"</c>. Copilot
    ///         rejects "max" universally — strip it everywhere.</item>
    ///   <item>No model — including opus-4.8 — accepts non-first
    ///         <c>role:"system"</c> messages. Copilot's gateway rejects them
    ///         with "Unexpected role 'system'" regardless of model. So
    ///         <see cref="ModelProfile.AcceptsMidConversationSystem"/> is
    ///         <c>false</c> for every entry; the fold runs unconditionally.</item>
    ///   <item><c>thinking.budget_tokens</c> must always be &lt;
    ///         <c>max_tokens</c>; otherwise Copilot 400s on that constraint
    ///         before evaluating the shape at all.</item>
    /// </list>
    /// </remarks>
    private static IEnumerable<ModelProfile> BuildDefault()
    {
        // ── Family: "enabled-only" thinking, no reasoning_effort ─────────
        // haiku-4.5, sonnet-4.5, opus-4.5 share the same wire contract:
        //   adaptive thinking rejected ("does not match expected tags:
        //     'disabled', 'enabled'" / "adaptive thinking is not supported"),
        //   enabled+disabled accepted,
        //   output_config.effort rejected outright ("does not support
        //     reasoning effort").
        yield return new ModelProfile
        {
            CanonicalId = "claude-haiku-4.5",
            AcceptedEfforts = [],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.EnabledOnly,
            MaxThinkingBudget = 32000, // /models capabilities.supports.max_thinking_budget
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-sonnet-4.5",
            AcceptedEfforts = [],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.EnabledOnly,
            MaxThinkingBudget = 32000,
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.5",
            AcceptedEfforts = [],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.EnabledOnly,
            MaxThinkingBudget = 32000,
        };

        // ── Family: "all thinking shapes" + low/medium/high effort ───────
        // sonnet-4.6 and opus-4.6 (base + 1m) accept all three thinking
        // shapes AND the three lower effort levels. xhigh and max → 400
        // ("supported values: [low medium high]") → strip; no -high/-xhigh
        // variants exist for this family, so RouteToVariant isn't useful.
        yield return new ModelProfile
        {
            CanonicalId = "claude-sonnet-4.6",
            AcceptedEfforts = ["low", "medium", "high"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.All,
            MaxThinkingBudget = 32000,
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.6",
            AcceptedEfforts = ["low", "medium", "high"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.All,
            MaxThinkingBudget = 32000,
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.6-1m",
            AcceptedEfforts = ["low", "medium", "high"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.All,
            MaxThinkingBudget = 32000,
            StripBetas = ["context-1m-*"],
        };

        // ── opus-4.7 family ──────────────────────────────────────────────
        // Adaptive-only thinking ("thinking.type.enabled is not supported …
        // Use thinking.type.adaptive and output_config.effort"). The base
        // model accepts ONLY effort=medium; high/xhigh route to dedicated
        // sibling ids; low has no sibling → strip (the model uses default
        // medium). The 1m-internal variant is special: it accepts effort
        // low/medium/high/xhigh on its own (no variant routing needed).
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.7",
            AcceptedEfforts = ["medium"],
            EffortOnUnsupported = EffortHandling.RouteToVariant,
            EffortToVariant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // low → no sibling exists → adjuster falls through to strip
                ["high"]  = "claude-opus-4.7-high",
                ["xhigh"] = "claude-opus-4.7-xhigh",
            },
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
        };
        // Variant-locked siblings — each accepts only its locked effort, so
        // strip every other value. Adaptive-only thinking, same as base.
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.7-high",
            AcceptedEfforts = ["high"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.7-xhigh",
            AcceptedEfforts = ["xhigh"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.7-1m-internal",
            AcceptedEfforts = ["low", "medium", "high", "xhigh"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
            StripBetas = ["context-1m-*"],
        };

        // ── opus-4.8 ─────────────────────────────────────────────────────
        // Same thinking contract as the opus-4.7 base (adaptive-only;
        // rejects enabled with the exact same message). Effort: ONLY medium
        // accepted; no -high/-xhigh siblings exist on Copilot for 4.8, so
        // every other value gets stripped. Mid-conversation system messages
        // are REJECTED even on 4.8 — Copilot's gateway hasn't picked up that
        // 4.8 protocol extension. Fold runs.
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.8",
            AcceptedEfforts = ["medium"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
            AcceptsMidConversationSystem = false, // empirical: Copilot rejects role:"system" mid-conv even on 4.8
            AcceptsSpeedFast = false,             // DTO doesn't model speed; unverified, leaving conservative
        };
    }
}

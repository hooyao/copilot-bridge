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
    ///   <item>Effort acceptance was re-probed 2026-06-05 (Copilot had widened
    ///         it since the catalog was first built). The current matrix is per
    ///         <c>ModelProfileProbe.Family_Effort_ReProbe</c> /
    ///         <c>Opus48_Effort_ReProbe</c>: opus-4.6 / opus-4.6-1m / sonnet-4.6
    ///         accept <c>low/medium/high/max</c> but REJECT <c>xhigh</c>
    ///         (effort tiers are NOT monotonic — <c>max</c> works where
    ///         <c>xhigh</c> 400s); opus-4.7 base / opus-4.7-1m-internal /
    ///         opus-4.8 accept all of <c>low/medium/high/xhigh/max</c>;
    ///         haiku-4.5 / sonnet-4.5 / opus-4.5 reject the effort field
    ///         entirely. "max" is no longer stripped universally — only on the
    ///         models that actually reject it.</item>
    ///   <item>Of the Anthropic models on Copilot, <b>only</b> opus-4.8 accepts
    ///         non-first <c>role:"system"</c> messages — and even there, only
    ///         in legal placements (predecessor=user, successor=assistant or
    ///         end-of-array). Every other model 400s with "Unexpected role
    ///         'system'" regardless of position, so
    ///         <see cref="ModelProfile.AcceptsMidConversationSystem"/> is
    ///         <c>true</c> for opus-4.8 and <c>false</c> for all others;
    ///         <see cref="Routing.ProfileAdjuster"/> converts mid-conv system
    ///         to user (with an injected-context marker prefix) when the
    ///         profile says <c>false</c>, and only placement-fixes the bad
    ///         positions when <c>true</c>.</item>
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
            // Copilot has no 1M variant for haiku/sonnet (every base model =
            // 200k input per /models; only opus-4.6-1m / opus-4.7-1m-internal
            // = 1M). Claude Code still offers "<family>[1m]" and, when picked,
            // sends bare model + context-1m-2025-08-07 on the wire. Copilot
            // accepts-and-ignores that token (returns 200), but forwarding a
            // beta the backend can't honor is misleading, so strip it. NOTE:
            // this does NOT change Claude Code's own 1M belief (that is decided
            // client-side from the [1m] suffix, before the request); the user
            // overfills to 200k and Copilot returns a "prompt is too long: N >
            // 200000" 400 that Claude Code self-heals on. See docs/context-window.md.
            StripBetas = ["context-1m-*"],
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-sonnet-4.5",
            AcceptedEfforts = [],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.EnabledOnly,
            MaxThinkingBudget = 32000,
            StripBetas = ["context-1m-*"], // no 1M sonnet on Copilot — see claude-haiku-4.5 above
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.5",
            AcceptedEfforts = [],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.EnabledOnly,
            MaxThinkingBudget = 32000,
        };

        // ── Family: "all thinking shapes" + effort low/medium/high/max ───
        // sonnet-4.6 and opus-4.6 (base + 1m) accept all three thinking
        // shapes. Effort re-probed 2026-06-05: low/medium/high/MAX accepted,
        // xhigh REJECTED ("supported values: [low medium high max]"). Note the
        // non-monotonicity — max works but xhigh doesn't, so AcceptedEfforts
        // lists max explicitly and the adjuster strips a stray xhigh. No
        // -high/-xhigh sibling ids exist for this family, so RouteToVariant
        // isn't useful.
        // Note on sonnet-4.6 context: Copilot serves sonnet-4.6 with native
        // 1M ctx — re-probed 2026-06-05 (851k-token padded prompt returns
        // 200; see ModelProfileProbe.NonOpus_LargePrompt_Probe200kBoundary).
        // PR #7's "no 1M sonnet on Copilot" + StripBetas=["context-1m-*"]
        // claim is now stale; the strip has been removed so a 1M-capable
        // beta hint passes through.
        yield return new ModelProfile
        {
            CanonicalId = "claude-sonnet-4.6",
            AcceptedEfforts = ["low", "medium", "high", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.All,
            MaxThinkingBudget = 32000,
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.6",
            AcceptedEfforts = ["low", "medium", "high", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.All,
            MaxThinkingBudget = 32000,
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.6-1m",
            AcceptedEfforts = ["low", "medium", "high", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.All,
            MaxThinkingBudget = 32000,
            StripBetas = ["context-1m-*"],
        };

        // ── opus-4.7 family ──────────────────────────────────────────────
        // Adaptive-only thinking ("thinking.type.enabled is not supported …
        // Use thinking.type.adaptive and output_config.effort"). Effort
        // re-probed 2026-06-05: the base model now accepts low/medium/high/
        // xhigh/max directly (Copilot widened it — it previously took only
        // medium). With every value accepted, the EffortToVariant routes to
        // the -high/-xhigh siblings are now DEAD (ApplyEffort returns early on
        // an accepted effort and never consults the map). They're left in
        // place — harmless and still correct if Copilot ever narrows the base
        // again — but the common path is now a direct accept, no sibling hop.
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.7",
            AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],
            EffortOnUnsupported = EffortHandling.RouteToVariant,
            EffortToVariant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Fallback siblings, only consulted if the base ever stops
                // accepting these directly again.
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
            AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
            StripBetas = ["context-1m-*"],
        };

        // ── opus-4.8 ─────────────────────────────────────────────────────
        // Same thinking contract as the opus-4.7 base (adaptive-only;
        // rejects enabled with the exact same message). Effort re-probed
        // 2026-06-05: low/medium/high/xhigh/MAX all accepted (Copilot widened
        // it — the catalog previously allowed only medium and was silently
        // stripping the user's requested max down to the model default). No
        // -high/-xhigh sibling ids exist for 4.8, but none are needed now that
        // the base takes every tier. Mid-conversation system messages are
        // ACCEPTED on 4.8 (Copilot enabled the 4.8 protocol extension; error
        // surface changed from "role unknown" to placement-specific errors).
        // Placement rule: <see cref="Routing.ProfileAdjuster"/> keeps S in
        // place when predecessor=user AND successor=assistant-or-end-of-array,
        // and converts to role:"user" with an injected-context prefix
        // otherwise. 1M context: opus-4.8 natively supports 1M ctx on Copilot
        // — no -1m-internal sibling exists or is needed (probed 2026-06-05: a
        // 260k-token prompt returns 200 with or without the
        // context-1m-2025-08-07 beta). So no StripBetas entry for
        // context-1m-* — the beta is silently accepted and the routing
        // table no longer downgrades opus-4.8 + 1M to 4.7-1m-internal.
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.8",
            AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
            AcceptsMidConversationSystem = true,  // empirical 2026-06-05: 4.8 accepts S in legal placements
            AcceptsSpeedFast = false,             // DTO doesn't model speed; unverified, leaving conservative
        };
    }
}

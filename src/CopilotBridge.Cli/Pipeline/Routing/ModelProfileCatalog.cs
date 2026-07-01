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
    /// Cross-cutting facts learned from probing the live Copilot model set (the
    /// 2026 reconciliation retired opus-4.5, opus-4.6-1m, and the opus-4.7
    /// -high/-xhigh/-1m-internal variants — all now 400 "not available for
    /// integrator"; see <c>ModelProfileProbe.RetiredCandidate_LivenessProbe</c>):
    /// <list type="bullet">
    ///   <item>Effort acceptance was re-probed 2026-06-05 and again during the
    ///         2026 reconciliation. Per
    ///         <c>ModelProfileProbe.Family_Effort_ReProbe</c> /
    ///         <c>Opus48_Effort_ReProbe</c> / <c>Sonnet5_Effort_ReProbe</c>:
    ///         opus-4.6 / sonnet-4.6 accept <c>low/medium/high/max</c> but REJECT
    ///         <c>xhigh</c> (effort tiers are NOT monotonic — <c>max</c> works
    ///         where <c>xhigh</c> 400s); opus-4.7 base / opus-4.8 / sonnet-5
    ///         accept all of <c>low/medium/high/xhigh/max</c> (sonnet-5 is the
    ///         first Sonnet-tier model to take <c>xhigh</c>); haiku-4.5 /
    ///         sonnet-4.5 reject the effort field entirely. "max" is not stripped
    ///         universally — only on the models that actually reject it.</item>
    ///   <item>Of the Anthropic models on Copilot, <b>opus-4.8 and sonnet-5</b>
    ///         accept non-first <c>role:"system"</c> messages — and even there,
    ///         only in legal placements (predecessor=user, successor=assistant or
    ///         end-of-array). Every other model 400s with "Unexpected role
    ///         'system'" regardless of position, so
    ///         <see cref="ModelProfile.AcceptsMidConversationSystem"/> is
    ///         <c>true</c> for opus-4.8 / sonnet-5 and <c>false</c> for all
    ///         others; <see cref="Routing.ProfileAdjuster"/> converts mid-conv
    ///         system to user (with an injected-context marker prefix) when the
    ///         profile says <c>false</c>, and only placement-fixes the bad
    ///         positions when <c>true</c>. (Note: sonnet-5 mid-conv support
    ///         contradicts Anthropic's "opus-4.8 only" docs — it was confirmed by
    ///         live probe, <c>Sonnet5_MidConversationSystem_PlacementRules</c>.)</item>
    ///   <item>1M context is now native on the opus-4.6 / opus-4.7 / opus-4.8 base
    ///         ids and on sonnet-5 (all serve &gt;600k-token prompts →&#160;200;
    ///         <c>OpusBase_LargePrompt_Probe…</c> / <c>Sonnet5_LargePrompt_Probe…</c>).
    ///         The dedicated <c>-1m</c> / <c>-1m-internal</c> ids that used to
    ///         unlock it were retired, so their redirects are gone — the base id
    ///         is passed through.</item>
    ///   <item><c>thinking.budget_tokens</c> must always be &lt;
    ///         <c>max_tokens</c>; otherwise Copilot 400s on that constraint
    ///         before evaluating the shape at all.</item>
    /// </list>
    /// </remarks>
    private static IEnumerable<ModelProfile> BuildDefault()
    {
        // ── Family: "enabled-only" thinking, no reasoning_effort ─────────
        // haiku-4.5, sonnet-4.5 share the same wire contract:
        //   adaptive thinking rejected ("does not match expected tags:
        //     'disabled', 'enabled'" / "adaptive thinking is not supported"),
        //   enabled+disabled accepted,
        //   output_config.effort rejected outright ("does not support
        //     reasoning effort").
        // (opus-4.5 was in this family but Copilot RETIRED it — 400
        //  model_not_supported; RetiredCandidate_LivenessProbe.)
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

        // ── claude-sonnet-5 ──────────────────────────────────────────────
        // Copilot's newest Sonnet (added to /models 2026, integrator allowlist
        // confirms). Despite the family name, its wire contract mirrors
        // OPUS-4.8, not sonnet-4.6 — every field below is live-probed
        // (ModelProfileProbe.Sonnet5_*), because /models capabilities lie:
        //   • Thinking: adaptive-ONLY. thinking.type.enabled → 400 ("not
        //     supported for this model. Use thinking.type.adaptive and
        //     output_config.effort"), same as opus-4.7/4.8 and UNLIKE sonnet-4.6
        //     (which is ThinkingPolicy.All). Sonnet5_Thinking_ProbeAcceptance:
        //     null/adaptive → 200, enabled → 400 (disabled 200).
        //   • Effort: low/medium/high/xhigh/max ALL accepted directly — the
        //     first Sonnet-tier model to take xhigh. Sonnet5_Effort_ReProbe:
        //     every tier → 200, standalone and with adaptive thinking.
        //   • Mid-conv system: ACCEPTED with the exact opus-4.8 placement rule
        //     (predecessor=user AND successor=assistant-or-end-of-array), which
        //     CONTRADICTS Anthropic's "opus-4.8 only" docs — hence we probe.
        //     Sonnet5_MidConversationSystem_PlacementRules: end-after-user (U·S)
        //     and U·A·U·S → 200; every predecessor=assistant / successor=user
        //     placement → 400 with the placement-specific ("must follow a 'user'
        //     message …" / "must precede an 'assistant' message …") errors, NOT
        //     the unconditional "Unexpected role 'system'". So true + ProfileAdjuster
        //     keeps legal placements and converts illegal ones (same path as 4.8).
        //   • 1M context: native. Sonnet5_ContextOneMillionBeta_ProbeAcceptance
        //     (baseline/with-beta/bogus-beta all 200 → Copilot ignores unknown
        //     betas, so the 1m acceptance is genuine) +
        //     Sonnet5_LargePrompt_ProbeOneMillionContextSupport (677k-token
        //     padded prompt → 200 with and without the beta). No -1m variant
        //     exists or is needed; no StripBetas entry — the context-1m beta is
        //     silently accepted, same as opus-4.8.
        yield return new ModelProfile
        {
            CanonicalId = "claude-sonnet-5",
            AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
            AcceptsMidConversationSystem = true,  // empirical 2026: placement-rule errors, same as opus-4.8
            AcceptsSpeedFast = false,             // DTO doesn't model speed; unverified, leaving conservative
        };
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.6",
            AcceptedEfforts = ["low", "medium", "high", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.All,
            MaxThinkingBudget = 32000,
        };
        // NOTE: claude-opus-4.6-1m was RETIRED by Copilot (2026 reconciliation:
        // 400 "not available for integrator" — RetiredCandidate_LivenessProbe).
        // Its profile is deleted. The opus-4.6 BASE id now serves 1M context
        // natively (OpusBase_LargePrompt_ProbeOneMillionContextSupport: 639k-token
        // prompt → 200 with and without the beta), so no 1M capability is lost —
        // the appsettings.json redirect that pointed here is removed too.

        // ── opus-4.7 family ──────────────────────────────────────────────
        // Adaptive-only thinking ("thinking.type.enabled is not supported …
        // Use thinking.type.adaptive and output_config.effort"). Effort
        // re-probed 2026-06-05: the base model accepts low/medium/high/xhigh/max
        // directly (Copilot widened it — it previously took only medium).
        // The -high / -xhigh / -1m-internal sibling ids were RETIRED by Copilot
        // (2026 reconciliation: all three 400 with "not available for integrator"
        // — RetiredCandidate_LivenessProbe), so their profiles are deleted and the
        // base no longer routes to them. Effort handling is now plain Strip (the
        // base accepts every tier directly, so a non-accepted value is impossible
        // for the Claude Code effort vocabulary — Strip is the safe no-op fallback).
        // 1M context: the base id serves 1M natively (OpusBase_LargePrompt_Probe…:
        // 677k-token prompt → 200), so the retired -1m-internal is not missed.
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-4.7",
            AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            MaxThinkingBudget = 32000,
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

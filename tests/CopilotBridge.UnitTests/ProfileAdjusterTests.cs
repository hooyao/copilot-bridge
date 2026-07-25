using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// <see cref="ProfileAdjuster.Apply"/> body coercion against the real
/// <see cref="ModelProfileCatalog"/>. The headline cases here guard the
/// derived-effort bug: for adaptive-only models, <c>thinking:enabled</c> is
/// coerced to <c>adaptive</c> and an <c>output_config.effort</c> is DERIVED
/// from the thinking budget — that derived value must still be validated
/// against the target's accepted efforts, or Copilot 400s
/// (<c>output_config.effort "high" is not supported by model
/// claude-opus-4.8; supported values: [medium]</c>). Reproduced live on
/// claude.exe 2.1.159: opus-4.8 + thinking:enabled budget=32000 → 400.
/// </summary>
public class ProfileAdjusterTests
{
    private static readonly ModelProfileCatalog Catalog = new();

    private static BridgeContext<MessagesRequest> WithThinking(
        string model, ThinkingConfig thinking, string? effort = null)
    {
        var ctx = TestCtx.Build(model, effort: effort);
        ctx.Request.Body = ctx.Request.Body with { Thinking = thinking };
        return ctx;
    }

    private static ModelProfile Adjust(BridgeContext<MessagesRequest> ctx, string profileId) =>
        ProfileAdjuster.Apply(ctx, Catalog.Get(profileId)!, Catalog);

    // ── The bug guarded here: derived effort must be re-validated against
    //    the target so an UNSUPPORTED value never leaks. As of the 2026-06-05
    //    effort re-probe, opus-4.7 base and opus-4.8 accept low..max directly,
    //    so a derived high/xhigh is now KEPT rather than stripped/routed — the
    //    invariant is still "the effort on the wire is one the model accepts",
    //    the post-widening outcome is just that more values qualify. ──────────

    [Fact]
    public void Opus48_ThinkingEnabledHighBudget_DerivedEffortIsAccepted()
    {
        // budget 32000 → BudgetToEffort → "high"; opus-4.8 now accepts "high"
        // directly (re-probed 2026-06-05), so the derived value is preserved.
        var ctx = WithThinking("claude-opus-4.8", new ThinkingConfigEnabled { BudgetTokens = 32000 });

        Adjust(ctx, "claude-opus-4.8");

        Assert.Equal("claude-opus-4.8", ctx.Request.Body.Model);
        Assert.IsType<ThinkingConfigAdaptive>(ctx.Request.Body.Thinking);
        // The invariant: whatever effort survives MUST be one opus-4.8 accepts.
        var effort = ctx.Request.Body.OutputConfig?.Effort;
        Assert.True(
            effort is null or "low" or "medium" or "high" or "xhigh" or "max",
            $"derived effort '{effort}' is not accepted by opus-4.8");
        Assert.Equal("high", effort);   // 32000 → high, now accepted as-is
    }

    [Fact]
    public void Opus47Base_ThinkingEnabledHighBudget_KeepsHighDirectly()
    {
        // opus-4.7 base now accepts "high" directly (re-probed 2026-06-05), so
        // the derived "high" stays on the base model — no sibling hop needed.
        // (EffortToVariant remains as a dormant fallback; it isn't consulted
        // while the base accepts the value.)
        var ctx = WithThinking("claude-opus-4.7", new ThinkingConfigEnabled { BudgetTokens = 32000 });

        Adjust(ctx, "claude-opus-4.7");

        Assert.Equal("claude-opus-4.7", ctx.Request.Body.Model);
        Assert.IsType<ThinkingConfigAdaptive>(ctx.Request.Body.Thinking);
        Assert.Equal("high", ctx.Request.Body.OutputConfig?.Effort);
    }

    [Fact]
    public void Opus47Base_ThinkingEnabledXhighBudget_KeepsXhighDirectly()
    {
        // budget 64000 → "xhigh"; opus-4.7 base now accepts xhigh directly.
        var ctx = WithThinking("claude-opus-4.7", new ThinkingConfigEnabled { BudgetTokens = 64000 });

        Adjust(ctx, "claude-opus-4.7");

        Assert.Equal("claude-opus-4.7", ctx.Request.Body.Model);
        Assert.Equal("xhigh", ctx.Request.Body.OutputConfig?.Effort);
    }

    [Fact]
    public void Opus48_ThinkingEnabledMediumBudget_KeepsAcceptedEffort()
    {
        // budget 16384 → "medium"; opus-4.8 accepts it → preserved (this is the
        // reasoning depth the derivation is meant to carry across the coerce).
        var ctx = WithThinking("claude-opus-4.8", new ThinkingConfigEnabled { BudgetTokens = 16384 });

        Adjust(ctx, "claude-opus-4.8");

        Assert.Equal("claude-opus-4.8", ctx.Request.Body.Model);
        Assert.IsType<ThinkingConfigAdaptive>(ctx.Request.Body.Thinking);
        Assert.Equal("medium", ctx.Request.Body.OutputConfig?.Effort);
    }

    // ── No regression: the re-run is idempotent for non-derivation paths ──

    [Fact]
    public void Opus47Base_ClientEffortHigh_NoThinking_KeptDirectly()
    {
        // opus-4.7 base now accepts effort=high directly (re-probed 2026-06-05),
        // so a client-sent "high" stays on the base model — no sibling hop.
        var ctx = TestCtx.Build("claude-opus-4.7", effort: "high");   // explicit, no thinking

        Adjust(ctx, "claude-opus-4.7");

        Assert.Equal("claude-opus-4.7", ctx.Request.Body.Model);
        Assert.Equal("high", ctx.Request.Body.OutputConfig?.Effort);
    }

    [Fact]
    public void Haiku45_ThinkingEnabled_StaysEnabled_NoEffortInvented()
    {
        // haiku-4.5 accepts thinking:enabled as-is → no coerce → no derivation.
        var ctx = WithThinking("claude-haiku-4.5", new ThinkingConfigEnabled { BudgetTokens = 32000 });

        Adjust(ctx, "claude-haiku-4.5");

        Assert.Equal("claude-haiku-4.5", ctx.Request.Body.Model);
        Assert.IsType<ThinkingConfigEnabled>(ctx.Request.Body.Thinking);
        Assert.Null(ctx.Request.Body.OutputConfig?.Effort);
    }

    // ── haiku-4.5 registers the context-1m strip (still 200k on Copilot) ─────

    [Theory]
    [InlineData("claude-haiku-4.5")]
    public void Haiku_RegisterContext1mStrip(string profileId)
    {
        var ctx = TestCtx.Build(profileId);

        Adjust(ctx, profileId);

        Assert.Contains("context-1m-*", ctx.PendingBetaStrips);
    }

    /// <summary>
    /// sonnet-4.6 must NOT strip the context-1m beta — Copilot re-probed
    /// 2026-06-05 to serve sonnet-4.6 with native 1M ctx (851k-token padded
    /// prompt returns 200; see <c>ModelProfileProbe.NonOpus_LargePrompt_Probe200kBoundary</c>).
    /// Stripping the beta the way PR #7 originally did would silently drop
    /// the only signal the bridge passes downstream about the client wanting
    /// 1M; identity passthrough is now correct.
    /// </summary>
    /// <summary>
    /// sonnet-5 must NOT strip the context-1m beta — it serves 1M natively
    /// (Sonnet5_LargePrompt_ProbeOneMillionContextSupport: 677k-token prompt →
    /// 200 with and without the beta), so identity passthrough is correct, same
    /// as opus-4.8 and sonnet-4.6.
    /// </summary>
    [Fact]
    public void Sonnet5_DoesNotStripContext1m()
    {
        var ctx = TestCtx.Build("claude-sonnet-5");

        Adjust(ctx, "claude-sonnet-5");

        Assert.DoesNotContain("context-1m-*", ctx.PendingBetaStrips);
    }

    /// <summary>
    /// sonnet-5 is adaptive-only (thinking.type.enabled → 400 —
    /// Sonnet5_Thinking_ProbeAcceptance), UNLIKE its sonnet-4.6 predecessor which
    /// takes enabled as-is. The contract: an inbound thinking:enabled must be
    /// coerced to adaptive, and the derived effort must be one sonnet-5 accepts.
    /// budget 32000 → BudgetToEffort → "high"; sonnet-5 accepts high directly.
    /// </summary>
    [Fact]
    public void Sonnet5_ThinkingEnabled_CoercedToAdaptive_DerivedEffortAccepted()
    {
        var ctx = WithThinking("claude-sonnet-5", new ThinkingConfigEnabled { BudgetTokens = 32000 });

        Adjust(ctx, "claude-sonnet-5");

        Assert.Equal("claude-sonnet-5", ctx.Request.Body.Model);
        Assert.IsType<ThinkingConfigAdaptive>(ctx.Request.Body.Thinking);
        var effort = ctx.Request.Body.OutputConfig?.Effort;
        Assert.True(
            effort is null or "low" or "medium" or "high" or "xhigh" or "max",
            $"derived effort '{effort}' is not accepted by sonnet-5");
        Assert.Equal("high", effort);
    }

    /// <summary>
    /// sonnet-5 accepts xhigh directly (the first Sonnet-tier model to —
    /// Sonnet5_Effort_ReProbe), so a client-sent xhigh survives untouched. This
    /// distinguishes it from sonnet-4.6, which would strip xhigh.
    /// </summary>
    [Fact]
    public void Sonnet5_ClientEffortXhigh_KeptDirectly()
    {
        var ctx = TestCtx.Build("claude-sonnet-5", effort: "xhigh");

        Adjust(ctx, "claude-sonnet-5");

        Assert.Equal("claude-sonnet-5", ctx.Request.Body.Model);
        Assert.Equal("xhigh", ctx.Request.Body.OutputConfig?.Effort);
    }

    /// <summary>
    /// Catalog fact: sonnet-5 accepts mid-conversation <c>role:"system"</c> (like
    /// opus-4.8), so <see cref="ProfileAdjuster"/> routes it through the
    /// keep-legal-placements path rather than converting every system message.
    /// Grounded in <c>Sonnet5_MidConversationSystem_PlacementRules</c> (legal
    /// placements → 200) — and contradicting Anthropic's "opus-4.8 only" docs,
    /// which is why this is a catalog fact, not an assumption. opus-4.8 stays
    /// true; every OTHER Copilot Anthropic model stays false.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-5", true)]
    [InlineData("claude-opus-4.8", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-sonnet-4.6", false)]
    [InlineData("claude-opus-4.7", false)]
    [InlineData("claude-opus-4.6", false)]
    [InlineData("claude-haiku-4.5", false)]
    public void MidConversationSystem_AcceptanceFlag_MatchesProbedContract(string id, bool accepts)
    {
        var profile = Catalog.Get(id);
        Assert.NotNull(profile);
        Assert.Equal(accepts, profile!.AcceptsMidConversationSystem);
    }

    // ── opus-5: the cross-field thinking-disabled × effort constraint ─────────
    //
    // CONTRACT (probed 2026-07, Opus5_DisabledThinking_EffortInteraction_Probe):
    // Copilot rejects effort xhigh/max on opus-5 *when thinking is disabled* —
    //   400 "output_config.effort 'max' is not supported when thinking is
    //        disabled on this model. Use effort 'high' or below, or enable
    //        thinking."
    // — while accepting those same efforts with thinking ON, and accepting
    // disabled thinking at high and below. So the bridge MUST NOT put that pair
    // on the wire. It must clamp DOWN to the highest still-accepted tier rather
    // than strip: the backend's own error names "effort 'high' or below" as the
    // remedy, and stripping would fall back to the model default, discarding
    // more of the user's requested depth than the constraint requires.
    //
    // Claude Code emits exactly this pair whenever a user disables thinking at
    // max/xhigh effort, so without the clamp every such turn 400s upstream.

    [Theory]
    [InlineData("max")]
    [InlineData("xhigh")]
    public void Opus5_DisabledThinking_RejectedEffort_ClampedToHigh(string inboundEffort)
    {
        var ctx = WithThinking("claude-opus-5", new ThinkingConfigDisabled(), effort: inboundEffort);

        Adjust(ctx, "claude-opus-5");

        // Thinking stays disabled — the clamp resolves the conflict by lowering
        // effort, never by silently re-enabling reasoning the user turned off.
        Assert.IsType<ThinkingConfigDisabled>(ctx.Request.Body.Thinking);
        // "high" is the highest tier opus-5 accepts under disabled thinking.
        Assert.Equal("high", ctx.Request.Body.OutputConfig?.Effort);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void Opus5_DisabledThinking_AcceptedEffort_PassesThroughUntouched(string inboundEffort)
    {
        // The constraint binds ONLY on xhigh/max. Everything at high or below is
        // legal with thinking off and must survive byte-identical — a clamp that
        // also touched these would be silently downgrading valid requests.
        var ctx = WithThinking("claude-opus-5", new ThinkingConfigDisabled(), effort: inboundEffort);

        Adjust(ctx, "claude-opus-5");

        Assert.IsType<ThinkingConfigDisabled>(ctx.Request.Body.Thinking);
        Assert.Equal(inboundEffort, ctx.Request.Body.OutputConfig?.Effort);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("xhigh")]
    public void Opus5_ThinkingOn_HighEfforts_SurviveUnclamped(string inboundEffort)
    {
        // The load-bearing half of the contract: with thinking ON, xhigh/max are
        // accepted (Opus5_Effort_ReProbe → 200 for every tier, standalone and
        // with adaptive). Modelling the constraint by narrowing AcceptedEfforts
        // would break exactly this case — the COMMON one — so this test is what
        // stops the cheaper, wrong implementation.
        var ctx = WithThinking("claude-opus-5", new ThinkingConfigAdaptive(), effort: inboundEffort);

        Adjust(ctx, "claude-opus-5");

        Assert.IsType<ThinkingConfigAdaptive>(ctx.Request.Body.Thinking);
        Assert.Equal(inboundEffort, ctx.Request.Body.OutputConfig?.Effort);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("xhigh")]
    public void ProfileWithoutTheConstraint_DisabledThinking_HighEfforts_NotClamped(string inboundEffort)
    {
        // Scope guard: the clamp must key on the profile's
        // EffortsRejectedWhenThinkingDisabled and NOTHING else. A synthetic
        // profile identical to opus-5 except for an EMPTY rejected-effort list
        // must leave xhigh/max alone.
        //
        // Deliberately NOT written against the real opus-4.8 profile: that one
        // carries ThinkingPolicy.AdaptiveOnly, which coerces thinking:disabled →
        // adaptive before the clamp is even consulted, so the assertion would
        // hold no matter how the clamp were scoped — a vacuous pass. (Confirmed
        // by mutation: copying the constraint onto opus-4.8 left the suite
        // green.) Pinning the thinking policy here to AdaptiveOrDisabled keeps
        // the disabled shape alive to the clamp, so this test can only pass
        // because the scoping is right.
        var permissive = new ModelProfile
        {
            CanonicalId = "test-no-constraint",
            AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOrDisabled,
            MaxThinkingBudget = 32000,
            EffortsRejectedWhenThinkingDisabled = [],
        };
        var catalog = new ModelProfileCatalog([permissive]);
        var ctx = WithThinking("test-no-constraint", new ThinkingConfigDisabled(), effort: inboundEffort);

        ProfileAdjuster.Apply(ctx, permissive, catalog);

        Assert.IsType<ThinkingConfigDisabled>(ctx.Request.Body.Thinking);
        Assert.Equal(inboundEffort, ctx.Request.Body.OutputConfig?.Effort);
    }

    /// <summary>
    /// opus-5 is adaptive-only: <c>thinking.type.enabled</c> → 400 ("Use
    /// thinking.type.adaptive and output_config.effort" —
    /// <c>Opus5_Thinking_ProbeAcceptance</c>). Inbound enabled must be coerced to
    /// adaptive with the reasoning depth carried across as effort. budget 64000 →
    /// <c>BudgetToEffort</c> → "xhigh", which opus-5 accepts — and because the
    /// coerced shape is adaptive (not disabled), the cross-field clamp must NOT
    /// fire here. This is the interaction between the two rules.
    /// </summary>
    [Fact]
    public void Opus5_ThinkingEnabled_CoercedToAdaptive_XhighSurvivesClamp()
    {
        var ctx = WithThinking("claude-opus-5", new ThinkingConfigEnabled { BudgetTokens = 64000 });

        Adjust(ctx, "claude-opus-5");

        Assert.Equal("claude-opus-5", ctx.Request.Body.Model);
        Assert.IsType<ThinkingConfigAdaptive>(ctx.Request.Body.Thinking);
        Assert.Equal("xhigh", ctx.Request.Body.OutputConfig?.Effort);
    }

    /// <summary>
    /// opus-5 serves 1M context natively (677k-token prompt → 200 with and
    /// without the beta — <c>Opus5_LargePrompt_ProbeOneMillionContextSupport</c>),
    /// so the <c>context-1m-*</c> beta must pass through rather than be stripped.
    /// Same as opus-4.8 / sonnet-5; opposite of haiku-4.5.
    /// </summary>
    [Fact]
    public void Opus5_DoesNotStripContext1m()
    {
        var ctx = TestCtx.Build("claude-opus-5");

        Adjust(ctx, "claude-opus-5");

        Assert.DoesNotContain("context-1m-*", ctx.PendingBetaStrips);
    }

    /// <summary>
    /// Retirement guard: Copilot retired <c>claude-sonnet-4.5</c> (400
    /// <c>model_not_supported</c> — <c>RetiredCandidate_LivenessProbe</c>), so the
    /// catalog must not carry a profile for it. A profile would assert probed
    /// knowledge the bridge no longer has, and would let the adjuster coerce a
    /// body against a contract nobody has verified since the id went away.
    /// <para>Deleting it does NOT make the id work: the router keeps the requested
    /// id on the wire even when it borrows a neighbour's profile, so a
    /// sonnet-4.5 request still 400s upstream. That is the intended outcome —
    /// Copilot's own error is the honest answer, and an operator who needs the id
    /// to keep working adds an explicit <c>Routing.Locations</c> rewrite.</para>
    /// <para>The stale integrator-allowlist entry is not evidence to the contrary
    /// — that list also names opus-4.5 and claude-fable-5, which likewise 400.</para>
    /// </summary>
    [Fact]
    public void RetiredModels_HaveNoProfile()
    {
        Assert.Null(Catalog.Get("claude-sonnet-4.5"));
        Assert.Null(Catalog.Get("claude-opus-4.5"));
        Assert.Null(Catalog.Get("claude-opus-4.6-1m"));
        Assert.Null(Catalog.Get("claude-opus-4.7-1m-internal"));
    }
}

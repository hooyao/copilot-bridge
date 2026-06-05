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
    public void Sonnet45_ThinkingEnabled_StaysEnabled_NoEffortInvented()
    {
        // sonnet-4.5 accepts thinking:enabled as-is → no coerce → no derivation.
        var ctx = WithThinking("claude-sonnet-4.5", new ThinkingConfigEnabled { BudgetTokens = 32000 });

        Adjust(ctx, "claude-sonnet-4.5");

        Assert.Equal("claude-sonnet-4.5", ctx.Request.Body.Model);
        Assert.IsType<ThinkingConfigEnabled>(ctx.Request.Body.Thinking);
        Assert.Null(ctx.Request.Body.OutputConfig?.Effort);
    }

    // ── sonnet-4.5/haiku-4.5 register the context-1m strip (still 200k on Copilot) ─────

    [Theory]
    [InlineData("claude-sonnet-4.5")]
    [InlineData("claude-haiku-4.5")]
    public void Sonnet45Haiku_RegisterContext1mStrip(string profileId)
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
    [Fact]
    public void Sonnet46_DoesNotStripContext1m()
    {
        var ctx = TestCtx.Build("claude-sonnet-4.6");

        Adjust(ctx, "claude-sonnet-4.6");

        Assert.DoesNotContain("context-1m-*", ctx.PendingBetaStrips);
    }
}

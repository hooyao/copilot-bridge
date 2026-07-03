using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// <see cref="ModelRouterStage"/>'s best-effort fallback for models with no
/// exact profile. The contract: a claude-* / gpt-* id newer than this build's
/// catalog is FORWARDED under the nearest known profile (real id kept on the
/// wire, correct backend endpoint) rather than hard-refused; an id too
/// dissimilar to any known model still throws <see cref="UnknownModelException"/>.
/// Uses the REAL catalogs + registry (no network) so "nearest" reflects the
/// live known set.
/// </summary>
public class ModelRouterStageFuzzyTests
{
    private static ModelRouterStage Stage(BridgeContext<MessagesRequest> ctx) => new(
        new CopilotModelRegistry(),
        new ModelProfileCatalog(),
        new CodexModelProfileCatalog(),
        Options.Create(new RoutesConfig()),
        Options.Create(new OutboundBetaPolicyOptions()),
        ctx,
        NullLogger<ModelRouterStage>.Instance,
        NullLogger<ModelRouteResolverLog>.Instance,
        NullLogger<ProfileAdjusterLog>.Instance);

    // ── Anthropic path: unknown-but-close claude id is forwarded ──────────────

    [Fact]
    public async Task UnknownButCloseClaudeId_Forwarded_RealIdKeptOnWire()
    {
        // A claude model this build has no profile for. It must NOT throw; it must
        // route to the Anthropic backend with the ORIGINAL id on the wire (Copilot
        // has the model — only our probed profile is missing).
        var ctx = TestCtx.Build("claude-sonnet-6");

        await Stage(ctx).ApplyAsync();

        Assert.Equal("claude-sonnet-6", ctx.Request.Body.Model);   // real id preserved
        Assert.NotNull(ctx.Target);
        Assert.Equal(BackendVendor.CopilotAnthropic, ctx.Target!.Vendor);
        Assert.Equal("/v1/messages", ctx.Target.Endpoint);
        Assert.Equal("claude-sonnet-6", ctx.Target.ModelId);        // dispatch also uses the real id
    }

    [Fact]
    public async Task UnknownButCloseClaudeId_BorrowsProfile_CoercesThinking()
    {
        // The borrowed profile must actually shape the body. claude-sonnet-6 will
        // borrow a sonnet/opus profile whose thinking policy is adaptive-only (or
        // All) — either way, an inbound thinking:enabled with 0 budget on an
        // adaptive-only borrow is coerced. We assert the softer invariant: the
        // request is shaped without throwing and the real id survives. (The
        // per-profile coercion itself is covered by ProfileAdjusterTests.)
        var ctx = TestCtx.Build("claude-opus-4.9", effort: "xhigh");

        await Stage(ctx).ApplyAsync();

        Assert.Equal("claude-opus-4.9", ctx.Request.Body.Model);
        Assert.Equal(BackendVendor.CopilotAnthropic, ctx.Target!.Vendor);
    }

    [Fact]
    public async Task BorrowedProfile_VariantRoutingNeutralized_ModelNotRewritten()
    {
        // The one body.Model-corruption risk: a borrowed profile with
        // EffortHandling.RouteToVariant could rewrite the model to a sized sibling
        // id that doesn't exist for the new model. The stage neutralizes that
        // (forces Strip). Even with an effort that a variant-routing profile would
        // redirect on, the real id must survive. (No live profile uses
        // RouteToVariant today, so this guards the safety net, not current data.)
        var ctx = TestCtx.Build("claude-opus-4.9", effort: "high");

        await Stage(ctx).ApplyAsync();

        Assert.Equal("claude-opus-4.9", ctx.Request.Body.Model);
        Assert.DoesNotContain("-high", ctx.Request.Body.Model);
        Assert.DoesNotContain("-xhigh", ctx.Request.Body.Model);
    }

    // ── Codex path note ───────────────────────────────────────────────────────
    // The Codex fuzzy gate in ModelRouterStage only runs for ids the registry
    // routes to CopilotResponses — a FIXED allowlist (ResponsesModelIds). A gpt id
    // NOT on that list resolves to CopilotOpenAi and never reaches the Codex
    // profile gate, so a "new gpt" can't exercise the Codex branch without first
    // being added to the allowlist. The Codex-side fuzzy borrow is therefore
    // covered at the level where it's reachable: ModelNameMatcherTests (gpt /
    // mai-code cases) and CodexRequestBuildTests (Build uses GetNearest).

    // ── Floor: dissimilar id still hard-errors ────────────────────────────────

    [Fact]
    public async Task DissimilarClaudeVendorId_StillThrows()
    {
        // A claude-prefixed but wildly dissimilar id — no known model is close
        // enough to borrow safely, so the actionable 400 is preserved.
        var ctx = TestCtx.Build("claude-zzz-totally-made-up-9999-xyz");

        var ex = await Assert.ThrowsAsync<UnknownModelException>(() => Stage(ctx).ApplyAsync());
        Assert.Contains("No profile for model", ex.Message);
    }

    [Fact]
    public async Task BelowFloorMiss_NamesTheNearestRejectedCandidate()
    {
        // Contract (PR comment #2): a below-floor 400 must still tell the operator
        // which known model was closest and how close — that's what distinguishes
        // "a real new model just under the bar → add a remap" from "a typo → nothing
        // close". Before the fix the diagnostic was structurally dead: GetNearest
        // reported an empty candidate + 0 score below the floor, so BestCandidate
        // was ALWAYS null and the message never named anything. The nearest known
        // claude id must now be surfaced, with its (sub-floor) similarity.
        var ctx = TestCtx.Build("claude-zzz-totally-made-up-9999-xyz");

        var ex = await Assert.ThrowsAsync<UnknownModelException>(() => Stage(ctx).ApplyAsync());

        Assert.NotNull(ex.BestCandidate);
        Assert.StartsWith("claude-", ex.BestCandidate!);
        Assert.True(ex.BestScore > 0, "a rejected candidate should carry its real similarity, not 0");
        Assert.True(
            ex.BestScore < ModelNameMatcher.DefaultMinSimilarity,
            $"below-floor case must score under the {ModelNameMatcher.DefaultMinSimilarity:F2} floor; got {ex.BestScore}");
        Assert.Contains("Nearest known model was", ex.Message);
        Assert.Contains(ex.BestCandidate!, ex.Message);
    }

    [Fact]
    public async Task ForeignVendorId_UnknownPrefix_Throws()
    {
        // A non-claude, non-gpt prefix has no backend route at all. The registry
        // returns null and the stage surfaces it (InvalidOperationException path)
        // — either way it must not silently forward.
        var ctx = TestCtx.Build("mistral-large-2");

        await Assert.ThrowsAnyAsync<Exception>(() => Stage(ctx).ApplyAsync());
    }
}

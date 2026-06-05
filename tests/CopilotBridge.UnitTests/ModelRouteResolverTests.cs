using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// <see cref="ModelRouteResolver.Apply"/> behavior: first-match-wins, and the
/// <c>Use</c> change-set actually mutates the context (model swap, per-target
/// EffortMap applied after the swap, header Set/Remove routed to the right
/// side-channel). These are the steps a misfire would silently corrupt.
/// </summary>
public class ModelRouteResolverTests
{
    private static RoutesConfig OneLoc(MatchExpression when, LocationUse use) =>
        new() { Locations = [new RouteLocation { When = when, Use = use }] };

    /// <summary>
    /// opus-4.7 OR opus-4.8, AND the 1M beta — a representative nested shape
    /// for exercising <see cref="ModelRouteResolver.Apply"/>. Note this is
    /// kept as a TEST FIXTURE only; production appsettings.json no longer
    /// routes opus-4.8 through this rule because Copilot's opus-4.8 natively
    /// supports 1M context (probed 2026-06-05 — see
    /// <c>tests/CopilotBridge.Playground/ModelProfileProbe.Opus48_LargePrompt_ProbeOneMillionContextSupport</c>).
    /// </summary>
    private static MatchExpression OneMWhen() => new()
    {
        AllOf =
        [
            new MatchExpression { AnyOf = [new() { Model = "claude-opus-4.7" }, new() { Model = "claude-opus-4.8" }] },
            new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Contains = "context-1m-2025-08-07" } },
        ],
    };

    [Fact]
    public void NestedWhen_Opus48_Match_SwapsModel_AndMapsMaxToXhigh()
    {
        var cfg = OneLoc(OneMWhen(),
            new LocationUse { Model = "claude-opus-4.7-1m-internal", EffortMap = new() { ["max"] = "xhigh" } });
        var ctx = TestCtx.Build("claude-opus-4.8", effort: "max", betas: ["context-1m-2025-08-07"]);

        var (matched, idx) = ModelRouteResolver.Apply(ctx, cfg);

        Assert.NotNull(matched);
        Assert.Equal(0, idx);
        Assert.Equal("claude-opus-4.7-1m-internal", ctx.Request.Body.Model);
        Assert.Equal("xhigh", ctx.Request.Body.OutputConfig?.Effort);   // EffortMap applied
    }

    [Fact]
    public void NestedWhen_WrongModel_DoesNotMatch()
    {
        var cfg = OneLoc(OneMWhen(), new LocationUse { Model = "claude-opus-4.7-1m-internal" });
        var ctx = TestCtx.Build("claude-opus-4.6", betas: ["context-1m-2025-08-07"]);

        var (matched, idx) = ModelRouteResolver.Apply(ctx, cfg);

        Assert.Null(matched);
        Assert.Equal(-1, idx);
        Assert.Equal("claude-opus-4.6", ctx.Request.Body.Model);   // unchanged
    }

    [Fact]
    public void FirstMatchWins_NoChain()
    {
        var cfg = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation { When = new() { Model = "claude-opus-4.7" }, Use = new() { Model = "FIRST" } },
                new RouteLocation { When = new() { Model = "claude-opus-4.7" }, Use = new() { Model = "SECOND" } },
            ],
        };
        var ctx = TestCtx.Build("claude-opus-4.7");

        var (_, idx) = ModelRouteResolver.Apply(ctx, cfg);

        Assert.Equal(0, idx);
        Assert.Equal("FIRST", ctx.Request.Body.Model);   // second location never runs
    }

    [Fact]
    public void EffortMap_NonMatchingEffort_LeavesItAlone()
    {
        var cfg = OneLoc(new MatchExpression { Model = "claude-opus-4.8" },
            new LocationUse { Model = "claude-opus-4.7-1m-internal", EffortMap = new() { ["max"] = "xhigh" } });
        var ctx = TestCtx.Build("claude-opus-4.8", effort: "high");   // "high" not in the map

        ModelRouteResolver.Apply(ctx, cfg);

        Assert.Equal("claude-opus-4.7-1m-internal", ctx.Request.Body.Model);
        Assert.Equal("high", ctx.Request.Body.OutputConfig?.Effort);   // untouched
    }

    [Fact]
    public void HeaderSet_AnthropicBeta_StagedAsBetaAdds()
    {
        var cfg = OneLoc(new MatchExpression { Model = "x" },
            new LocationUse { Headers = new LocationHeaders { Set = new() { ["anthropic-beta"] = "foo-2026,bar-2026" } } });
        var ctx = TestCtx.Build("x");

        ModelRouteResolver.Apply(ctx, cfg);

        Assert.Contains("foo-2026", ctx.PendingBetaAdds);
        Assert.Contains("bar-2026", ctx.PendingBetaAdds);
    }

    [Fact]
    public void HeaderSet_IdentityHeader_StagedAsCopilotOverride()
    {
        var cfg = OneLoc(new MatchExpression { Model = "x" },
            new LocationUse { Headers = new LocationHeaders { Set = new() { ["Editor-Version"] = "vscode/2.0.0" } } });
        var ctx = TestCtx.Build("x");

        ModelRouteResolver.Apply(ctx, cfg);

        Assert.Equal("vscode/2.0.0", ctx.CopilotHeaderOverrides["Editor-Version"]);
    }

    [Fact]
    public void HeaderRemove_BetaTokenForm_StagedAsBetaStrip()
    {
        var cfg = OneLoc(new MatchExpression { Model = "x" },
            new LocationUse { Headers = new LocationHeaders { Remove = ["anthropic-beta:context-1m-*"] } });
        var ctx = TestCtx.Build("x");

        ModelRouteResolver.Apply(ctx, cfg);

        Assert.Contains("context-1m-*", ctx.PendingBetaStrips);
    }

    [Fact]
    public void HeaderRemove_WholeIdentityHeader_StagedAsNullOverride()
    {
        var cfg = OneLoc(new MatchExpression { Model = "x" },
            new LocationUse { Headers = new LocationHeaders { Remove = ["Editor-Version"] } });
        var ctx = TestCtx.Build("x");

        ModelRouteResolver.Apply(ctx, cfg);

        Assert.True(ctx.CopilotHeaderOverrides.ContainsKey("Editor-Version"));
        Assert.Null(ctx.CopilotHeaderOverrides["Editor-Version"]);   // null = "drop this header"
    }
}

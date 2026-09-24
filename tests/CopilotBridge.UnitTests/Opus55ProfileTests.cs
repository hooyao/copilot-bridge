using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>Copilot /v1/messages contract for the account's Opus 5.5 model.</summary>
public sealed class Opus55ProfileTests
{
    private static readonly ModelProfileCatalog Catalog = new();
    private static readonly ModelProfile Profile = Catalog.Get("claude-opus-5.5")!;

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-opus-5-5-20260922")]
    [InlineData("claude-opus-5.5")]
    public void ClaudeClientId_ResolvesToCopilotDottedId(string clientId)
    {
        var target = new CopilotModelRegistry().Resolve(clientId);

        Assert.NotNull(target);
        Assert.Equal("/v1/messages", target!.Endpoint);
        Assert.Equal("claude-opus-5.5", target.ModelId);
        Assert.NotNull(Catalog.Get(target.ModelId));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void EveryProbedEffort_ReachesCopilot(string effort)
    {
        var ctx = TestCtx.Build("claude-opus-5.5", effort: effort);

        ProfileAdjuster.Apply(ctx, Profile, Catalog);

        Assert.Equal(effort, ctx.Request.Body.OutputConfig?.Effort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedThinking_IsConvertedToAdaptive(bool disabled)
    {
        var ctx = TestCtx.Build("claude-opus-5.5", effort: "max");
        ctx.Request.Body = ctx.Request.Body with
        {
            Thinking = disabled
                ? new ThinkingConfigDisabled()
                : new ThinkingConfigEnabled { BudgetTokens = 64000 },
        };

        ProfileAdjuster.Apply(ctx, Profile, Catalog);

        Assert.IsType<ThinkingConfigAdaptive>(ctx.Request.Body.Thinking);
        Assert.Equal(disabled ? "low" : "xhigh", ctx.Request.Body.OutputConfig?.Effort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForcedToolChoice_BecomesAuto_AndKeepsParallelSetting(bool specificTool)
    {
        var ctx = TestCtx.Build("claude-opus-5.5");
        ctx.Request.Body = ctx.Request.Body with
        {
            ToolChoice = specificTool
                ? new ToolChoiceTool { Name = "lookup", DisableParallelToolUse = true }
                : new ToolChoiceAny { DisableParallelToolUse = true },
        };

        ProfileAdjuster.Apply(ctx, Profile, Catalog);

        var choice = Assert.IsType<ToolChoiceAuto>(ctx.Request.Body.ToolChoice);
        Assert.True(choice.DisableParallelToolUse);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SupportedToolChoice_PassesThrough(bool auto)
    {
        var ctx = TestCtx.Build("claude-opus-5.5");
        ToolChoice inbound = auto
            ? new ToolChoiceAuto { DisableParallelToolUse = true }
            : new ToolChoiceNone();
        ctx.Request.Body = ctx.Request.Body with { ToolChoice = inbound };

        ProfileAdjuster.Apply(ctx, Profile, Catalog);

        Assert.Same(inbound, ctx.Request.Body.ToolChoice);
    }

    [Fact]
    public void NativeMillionContextBeta_IsNotStripped()
    {
        var ctx = TestCtx.Build("claude-opus-5.5");

        ProfileAdjuster.Apply(ctx, Profile, Catalog);

        Assert.DoesNotContain("context-1m-*", ctx.PendingBetaStrips);
    }
}

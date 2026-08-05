using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract for the shared messages/count route-planning seam. Equal request
/// inputs must produce equal targets, while concurrent contexts retain their
/// own model, effort, beta, and header mutations.
/// </summary>
public sealed class ModelRoutePlannerParityTests
{
    private static readonly RoutesConfig Routes = new()
    {
        Locations =
        [
            new RouteLocation
            {
                When = new MatchExpression
                {
                    Model = "claude-opus-5",
                    Header = new HeaderMatch
                    {
                        Name = "anthropic-beta",
                        Contains = "token-counting-2024-11-01",
                    },
                    Effort = "max",
                },
                Use = new LocationUse
                {
                    Model = "gpt-5.6-sol",
                    EffortMap = new() { ["max"] = "xhigh" },
                    Headers = new LocationHeaders
                    {
                        Set = new() { ["anthropic-beta"] = "route-only-beta" },
                    },
                },
            },
        ],
    };

    [Fact]
    public async Task MessageStageAndCountPlanner_ResolveIdenticalRouteInputsEqually()
    {
        var planner = CountTokensTestServices.Planner(Routes);
        var message = Context(matches: true);
        var count = Context(matches: true);

        await new ModelRouterStage(planner, message).ApplyAsync();
        var countTarget = planner.Plan(count);

        Assert.Equal(message.Target, countTarget);
        Assert.Equal("gpt-5.6-sol", message.Request.Body.Model);
        Assert.Equal(message.Request.Body.Model, count.Request.Body.Model);
        Assert.Equal("xhigh", message.Request.Body.OutputConfig?.Effort);
        Assert.Equal(message.Request.Body.OutputConfig?.Effort,
            count.Request.Body.OutputConfig?.Effort);
        Assert.Equal(["route-only-beta"], message.PendingBetaAdds);
        Assert.Equal(message.PendingBetaAdds, count.PendingBetaAdds);
    }

    [Fact]
    public async Task ConcurrentPlans_DoNotShareMutableRouteState()
    {
        var planner = CountTokensTestServices.Planner(Routes);
        var contexts = Enumerable.Range(0, 200)
            .Select(i => Context(matches: i % 2 == 0))
            .ToArray();

        await Task.WhenAll(contexts.Select(ctx => Task.Run(() => planner.Plan(ctx))));

        for (var i = 0; i < contexts.Length; i++)
        {
            var ctx = contexts[i];
            if (i % 2 == 0)
            {
                Assert.Equal("gpt-5.6-sol", ctx.Request.Body.Model);
                Assert.Equal("xhigh", ctx.Request.Body.OutputConfig?.Effort);
                Assert.Equal(["route-only-beta"], ctx.PendingBetaAdds);
                Assert.Equal(BackendVendor.CopilotResponses, ctx.Target?.Vendor);
            }
            else
            {
                Assert.Equal("claude-opus-5", ctx.Request.Body.Model);
                Assert.Null(ctx.Request.Body.OutputConfig);
                Assert.Empty(ctx.PendingBetaAdds);
                Assert.Equal(BackendVendor.CopilotAnthropic, ctx.Target?.Vendor);
            }
        }
    }

    private static BridgeContext<MessagesRequest> Context(bool matches)
    {
        var betas = matches
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "token-counting-2024-11-01" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new BridgeContext<MessagesRequest>
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/cc/v1/messages",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Body = new MessagesRequest
                {
                    Model = "claude-opus-5",
                    Messages = [],
                    OutputConfig = matches
                        ? new OutputConfig { Effort = "max" }
                        : null,
                },
            },
            Response = new BridgeResponse(),
            InboundBetas = betas,
        };
    }
}

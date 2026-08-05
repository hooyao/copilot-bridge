using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Pipeline adapter around the shared route planner. It must run first because
/// later stages and the strategy registry consume <c>ctx.Target</c>.
/// </summary>
internal sealed class ModelRouterStage : IRequestStage<MessagesRequest>
{
    private readonly ModelRoutePlanner _planner;
    private readonly BridgeContext<MessagesRequest> _ctx;

    public ModelRouterStage(
        ModelRoutePlanner planner,
        BridgeContext<MessagesRequest> ctx)
    {
        _planner = planner;
        _ctx = ctx;
    }

    public string Name => "ModelRouter";

    public Task ApplyAsync()
    {
        _planner.Plan(_ctx);
        return Task.CompletedTask;
    }
}

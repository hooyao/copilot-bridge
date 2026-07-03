using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Drives a <see cref="Pipeline{TBody}"/>: runs request stages, resolves and
/// invokes the strategy, runs response stages. Endpoints populate the injected
/// <see cref="BridgeContext{TBody}"/> and hand the pipeline to a runner.
/// </summary>
internal interface IPipelineRunner<TBody> where TBody : class
{
    Task RunAsync(Pipeline<TBody> pipeline);
}

internal sealed class PipelineRunner<TBody> : IPipelineRunner<TBody> where TBody : class
{
    private readonly BridgeContext<TBody> _ctx;
    private readonly ILogger<PipelineRunner<TBody>> _log;

    public PipelineRunner(BridgeContext<TBody> ctx, ILogger<PipelineRunner<TBody>> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public async Task RunAsync(Pipeline<TBody> pipeline)
    {
        _log.LogDebug("pipeline {PipelineName} start  path={Path}  body-bytes={BodyBytes}",
            pipeline.Name, _ctx.Request.Path, _ctx.Request.RawBody.Length);

        foreach (var stage in pipeline.RequestStages)
        {
            _log.LogDebug("req-stage start  {StageName}", stage.Name);
            await stage.ApplyAsync();
            _log.LogDebug("req-stage end    {StageName}", stage.Name);
        }

        if (_ctx.Target is null)
        {
            throw new InvalidOperationException(
                "Pipeline finished request stages without resolving ctx.Target — "
                + "ensure ModelRouterStage (or equivalent) runs in the request stage list.");
        }

        var strategy = pipeline.Strategies.Resolve(_ctx.Target);
        _log.LogDebug("strategy resolved  {StrategyName}  target={Vendor}:{Endpoint}  model={ModelId}",
            strategy.Name, _ctx.Target.Vendor, _ctx.Target.Endpoint, _ctx.Target.ModelId);
        await strategy.ForwardAsync();
        _log.LogDebug("strategy returned  status={Status}  mode={Mode}",
            _ctx.Response.Status, _ctx.Response.Mode);

        foreach (var stage in pipeline.ResponseStages)
        {
            _log.LogDebug("resp-stage start {StageName}", stage.Name);
            await stage.ApplyAsync();
            _log.LogDebug("resp-stage end   {StageName}", stage.Name);
        }

        _log.LogDebug("pipeline {PipelineName} end", pipeline.Name);
    }
}

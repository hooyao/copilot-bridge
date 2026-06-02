using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Drives a <see cref="Pipeline{TBody}"/>: runs request stages, resolves and
/// invokes the strategy, runs response stages. Endpoints construct a
/// <see cref="BridgeContext{TBody}"/> and hand it to a runner.
/// </summary>
internal interface IPipelineRunner<TBody> where TBody : class
{
    Task RunAsync(Pipeline<TBody> pipeline, BridgeContext<TBody> ctx);
}

internal sealed class PipelineRunner<TBody> : IPipelineRunner<TBody> where TBody : class
{
    private readonly ILogger<PipelineRunner<TBody>> _log;

    public PipelineRunner(ILogger<PipelineRunner<TBody>> log)
    {
        _log = log;
    }

    public async Task RunAsync(Pipeline<TBody> pipeline, BridgeContext<TBody> ctx)
    {
        _log.LogDebug("pipeline {PipelineName} start  path={Path}  body-bytes={BodyBytes}",
            pipeline.Name, ctx.Request.Path, ctx.Request.RawBody.Length);

        foreach (var stage in pipeline.RequestStages)
        {
            _log.LogDebug("req-stage start  {StageName}", stage.Name);
            await stage.ApplyAsync(ctx);
            _log.LogDebug("req-stage end    {StageName}", stage.Name);
        }

        if (ctx.Target is null)
        {
            throw new InvalidOperationException(
                "Pipeline finished request stages without resolving ctx.Target — "
                + "ensure ModelRouterStage (or equivalent) runs in the request stage list.");
        }

        var strategy = pipeline.Strategies.Resolve(ctx.Target);
        _log.LogDebug("strategy resolved  {StrategyName}  target={Vendor}:{Endpoint}  model={ModelId}",
            strategy.Name, ctx.Target.Vendor, ctx.Target.Endpoint, ctx.Target.ModelId);
        await strategy.ForwardAsync(ctx);
        _log.LogDebug("strategy returned  status={Status}  mode={Mode}",
            ctx.Response.Status, ctx.Response.Mode);

        foreach (var stage in pipeline.ResponseStages)
        {
            _log.LogDebug("resp-stage start {StageName}", stage.Name);
            await stage.ApplyAsync(ctx);
            _log.LogDebug("resp-stage end   {StageName}", stage.Name);
        }

        _log.LogDebug("pipeline {PipelineName} end", pipeline.Name);
    }
}

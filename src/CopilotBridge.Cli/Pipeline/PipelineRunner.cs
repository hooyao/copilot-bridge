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
    public async Task RunAsync(Pipeline<TBody> pipeline, BridgeContext<TBody> ctx)
    {
        DiagTracer.Log($"pipeline {pipeline.Name} start  path={ctx.Request.Path}  body-bytes={ctx.Request.RawBody.Length}");

        foreach (var stage in pipeline.RequestStages)
        {
            DiagTracer.Log($"req-stage start  {stage.Name}");
            await stage.ApplyAsync(ctx);
            DiagTracer.Log($"req-stage end    {stage.Name}");
        }

        if (ctx.Target is null)
        {
            throw new InvalidOperationException(
                "Pipeline finished request stages without resolving ctx.Target — "
                + "ensure ModelRouterStage (or equivalent) runs in the request stage list.");
        }

        var strategy = pipeline.Strategies.Resolve(ctx.Target);
        DiagTracer.Log($"strategy resolved  {strategy.Name}  target={ctx.Target.Vendor}:{ctx.Target.Endpoint}  model={ctx.Target.ModelId}");
        await strategy.ForwardAsync(ctx);
        DiagTracer.Log($"strategy returned  status={ctx.Response.Status}  mode={ctx.Response.Mode}");

        foreach (var stage in pipeline.ResponseStages)
        {
            DiagTracer.Log($"resp-stage start {stage.Name}");
            await stage.ApplyAsync(ctx);
            DiagTracer.Log($"resp-stage end   {stage.Name}");
        }

        DiagTracer.Log($"pipeline {pipeline.Name} end");
    }
}

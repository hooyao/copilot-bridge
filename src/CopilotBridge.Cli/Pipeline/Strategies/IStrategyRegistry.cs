namespace CopilotBridge.Cli.Pipeline.Strategies;

/// <summary>
/// Resolves the right <see cref="IUpstreamStrategy{TBody}"/> for a
/// <see cref="RouteTarget"/>. The default implementation walks a registered
/// list and returns the first <see cref="IUpstreamStrategy{TBody}.Matches"/>;
/// if none matches, throws — pipelines should always include a catch-all
/// strategy that emits HTTP 400 with a clear "model not routable" message.
/// </summary>
internal interface IStrategyRegistry<TBody> where TBody : class
{
    IUpstreamStrategy<TBody> Resolve(RouteTarget target);
}

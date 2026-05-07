namespace CopilotBridge.Cli.Pipeline.Strategies;

/// <summary>
/// List-based <see cref="IStrategyRegistry{TBody}"/>. Walks registered
/// strategies in order; first <see cref="IUpstreamStrategy{TBody}.Matches"/>
/// wins. If none match, throws — pipelines are expected to register a
/// catch-all strategy that emits HTTP 400 with a descriptive message rather
/// than letting requests fall through.
/// </summary>
internal sealed class StrategyRegistry<TBody> : IStrategyRegistry<TBody> where TBody : class
{
    private readonly IReadOnlyList<IUpstreamStrategy<TBody>> _strategies;

    public StrategyRegistry(IEnumerable<IUpstreamStrategy<TBody>> strategies)
    {
        _strategies = [.. strategies];
    }

    public IUpstreamStrategy<TBody> Resolve(RouteTarget target)
    {
        foreach (var s in _strategies)
        {
            if (s.Matches(target)) return s;
        }
        throw new InvalidOperationException(
            $"No strategy matched target {target.Vendor}:{target.Endpoint} model={target.ModelId}. "
            + "Pipeline assembly is missing a handler for this route.");
    }
}

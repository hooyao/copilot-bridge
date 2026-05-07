using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Stages;
using CopilotBridge.Cli.Pipeline.Strategies;

namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// A specific assembly of stages, response stages, and a strategy registry —
/// one per (client shape, IR shape) combination, though in practice all three
/// of our pipelines flow on IR (= Anthropic shape) and we currently expect
/// only one concrete pipeline (<c>Pipeline&lt;MessagesRequest&gt;</c>) for M1.
/// </summary>
internal sealed class Pipeline<TBody> where TBody : class
{
    public required string Name { get; init; }
    public required IReadOnlyList<IRequestStage<TBody>> RequestStages { get; init; }
    public required IReadOnlyList<IResponseStage<TBody>> ResponseStages { get; init; }
    public required IStrategyRegistry<TBody> Strategies { get; init; }
}

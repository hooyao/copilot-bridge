namespace CopilotBridge.Cli.Pipeline.Strategies;

/// <summary>
/// Forwarder for one (IR shape, backend shape) pair. Owns the HTTP call to
/// Copilot. If the backend shape differs from the IR shape, the strategy
/// internally translates the request body before sending and wraps the
/// response stream with a reverse translator before assigning to
/// <c>ctx.Response</c>. After <see cref="ForwardAsync"/> returns,
/// <c>ctx.Response</c> is in the SAME shape (IR) the response stages expect.
/// </summary>
internal interface IUpstreamStrategy<TBody> where TBody : class
{
    string Name { get; }

    /// <summary>
    /// True if this strategy handles the given <see cref="RouteTarget"/>.
    /// The strategy registry asks each registered strategy in order until one
    /// matches.
    /// </summary>
    bool Matches(RouteTarget target);

    Task ForwardAsync(BridgeContext<TBody> ctx);
}

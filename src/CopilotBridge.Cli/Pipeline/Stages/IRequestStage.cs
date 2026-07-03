namespace CopilotBridge.Cli.Pipeline.Stages;

/// <summary>
/// One transformation step in the request pipeline. Stages mutate
/// <c>ctx.Request.Body</c>, <c>ctx.Request.Headers</c>, and
/// <c>ctx.Target</c> on the <b>injected</b> <c>BridgeContext</c>; they have no
/// other side effects beyond <see cref="DiagLog"/> calls (which are stripped from
/// Release builds).
/// </summary>
/// <remarks>
/// <typeparamref name="TBody"/> groups same-shape stages into a
/// <c>Pipeline&lt;TBody&gt;</c>; the concrete context (<c>BridgeContext&lt;TBody&gt;</c>)
/// is constructor-injected per request scope, not passed as a method argument.
/// </remarks>
internal interface IRequestStage<TBody> where TBody : class
{
    /// <summary>
    /// Stable identifier used in the diag log. Convention: type name without
    /// the <c>Stage</c> suffix (e.g. <c>"ModelRouter"</c>).
    /// </summary>
    string Name { get; }

    Task ApplyAsync();
}

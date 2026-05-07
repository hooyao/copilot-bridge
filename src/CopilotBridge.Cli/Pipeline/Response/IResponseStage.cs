namespace CopilotBridge.Cli.Pipeline.Response;

/// <summary>
/// One transformation step in the response pipeline. For streaming responses,
/// implementations wrap <c>ctx.Response.EventStream</c> with a transforming
/// async iterator; for non-streaming, they mutate
/// <c>ctx.Response.BufferedBody</c>. Stages should check
/// <c>ctx.Response.Mode</c> and short-circuit if they only handle one mode.
/// </summary>
/// <remarks>
/// <typeparamref name="TBody"/> is the IR-shape request body, kept in scope
/// for symmetry and so a response stage can consult what was originally
/// requested. Most stages ignore it.
/// </remarks>
internal interface IResponseStage<TBody> where TBody : class
{
    string Name { get; }

    Task ApplyAsync(BridgeContext<TBody> ctx);
}

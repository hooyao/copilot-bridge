namespace CopilotBridge.Cli.Pipeline.Adapters;

/// <summary>
/// Translates a parsed inbound request body from a client's native shape into
/// the bridge IR. Identity for clients whose shape == IR (Anthropic).
/// </summary>
/// <remarks>
/// Adapters are NOT pipeline stages — they run BEFORE the request pipeline,
/// transforming <typeparamref name="TClientBody"/> → <typeparamref name="TIR"/>.
/// Once converted, the pipeline runs uniformly on IR shape regardless of where
/// the request originated.
/// </remarks>
internal interface IClientInboundAdapter<TClientBody, TIR>
    where TClientBody : class
    where TIR : class
{
    string Name { get; }

    /// <summary>
    /// Convert the parsed client body to IR shape. Implementations may inspect
    /// <paramref name="headers"/> for client-specific hints (e.g. Codex's
    /// <c>OpenAI-Beta</c> values); they MUST NOT mutate the headers
    /// dictionary — that belongs to the endpoint, which copies into
    /// <c>BridgeContext.Request.Headers</c>.
    /// </summary>
    ValueTask<TIR> AdaptAsync(
        TClientBody clientBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct);
}

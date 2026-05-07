using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline.Adapters;

/// <summary>
/// Translates an IR-shape response back into the client's native wire shape.
/// Identity for clients whose shape == IR (Anthropic). Streaming variants are
/// stateful — IR→OpenAI must accumulate <c>input_json_delta</c> fragments
/// into a complete tool-call argument string before emitting the corresponding
/// OpenAI chunk.
/// </summary>
internal interface IClientOutboundAdapter<TIR> where TIR : class
{
    string Name { get; }

    /// <summary>
    /// Wraps the IR-shape event stream into a client-shape event stream.
    /// Response stages have already finished mutating <c>ctx.Response</c>;
    /// this is the last step before the endpoint writer flushes to the wire.
    /// </summary>
    IAsyncEnumerable<SseItem<string>> AdaptStreamAsync(
        IAsyncEnumerable<SseItem<string>> irStream,
        BridgeContext<TIR> ctx,
        CancellationToken ct);

    /// <summary>
    /// Buffered (non-streaming) variant. The endpoint picks this when
    /// <c>ctx.Response.Mode == Buffered</c>.
    /// </summary>
    ValueTask<byte[]> AdaptBufferedAsync(
        byte[] irBody,
        BridgeContext<TIR> ctx,
        CancellationToken ct);
}

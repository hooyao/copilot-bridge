using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Identity adapter — IR responses are already in the Anthropic shape Claude
/// Code expects, so both streaming and buffered modes return the input
/// unchanged. The streaming variant returns the same async sequence directly
/// (no copying / re-yielding) so consumption stays lazy.
/// </summary>
internal sealed class ClaudeCodeOutboundAdapter : IClientOutboundAdapter<MessagesRequest>
{
    public static readonly ClaudeCodeOutboundAdapter Instance = new();

    private ClaudeCodeOutboundAdapter() { }

    public string Name => "ClaudeCodeOutbound";

    public IAsyncEnumerable<SseItem<string>> AdaptStreamAsync(
        IAsyncEnumerable<SseItem<string>> irStream,
        BridgeContext<MessagesRequest> ctx,
        CancellationToken ct)
    {
        Log.Debug($"adapter {Name}: identity-stream");
        return irStream;
    }

    public ValueTask<byte[]> AdaptBufferedAsync(
        byte[] irBody,
        BridgeContext<MessagesRequest> ctx,
        CancellationToken ct)
    {
        Log.Debug($"adapter {Name}: identity-buffered  bytes={irBody.Length}");
        return ValueTask.FromResult(irBody);
    }
}

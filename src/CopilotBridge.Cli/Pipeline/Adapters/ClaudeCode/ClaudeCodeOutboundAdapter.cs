using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Identity adapter — IR responses are already in the Anthropic shape Claude
/// Code expects, so both streaming and buffered modes return the input
/// unchanged. The streaming variant returns the same async sequence directly
/// (no copying / re-yielding) so consumption stays lazy.
/// </summary>
internal sealed class ClaudeCodeOutboundAdapter : IClientOutboundAdapter<MessagesRequest>
{
    private readonly ILogger<ClaudeCodeOutboundAdapter> _log;

    public ClaudeCodeOutboundAdapter(ILogger<ClaudeCodeOutboundAdapter> log)
    {
        _log = log;
    }

    public string Name => "ClaudeCodeOutbound";

    public IAsyncEnumerable<SseItem<string>> AdaptStreamAsync(
        IAsyncEnumerable<SseItem<string>> irStream,
        CancellationToken ct)
    {
        _log.LogDebug("adapter {Name}: identity-stream", Name);
        return irStream;
    }

    public ValueTask<byte[]> AdaptBufferedAsync(
        byte[] irBody,
        CancellationToken ct)
    {
        _log.LogDebug("adapter {Name}: identity-buffered  bytes={Bytes}", Name, irBody.Length);
        return ValueTask.FromResult(irBody);
    }
}

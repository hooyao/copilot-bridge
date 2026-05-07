using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Identity adapter — Claude Code speaks the Anthropic Messages API, which is
/// also the bridge's IR shape, so the inbound side is a pure passthrough. This
/// class exists to establish the integration pattern (so adding Codex / Gemini
/// later doesn't require pipeline plumbing changes) and as the natural place
/// to drop client-specific inbound logic if it ever surfaces.
/// </summary>
internal sealed class ClaudeCodeInboundAdapter : IClientInboundAdapter<MessagesRequest, MessagesRequest>
{
    public static readonly ClaudeCodeInboundAdapter Instance = new();

    private ClaudeCodeInboundAdapter() { }

    public string Name => "ClaudeCodeInbound";

    public ValueTask<MessagesRequest> AdaptAsync(
        MessagesRequest clientBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        DiagTracer.Log($"adapter {Name}: identity  model={clientBody.Model}  messages={clientBody.Messages.Count}  stream={clientBody.Stream == true}");
        return ValueTask.FromResult(clientBody);
    }
}

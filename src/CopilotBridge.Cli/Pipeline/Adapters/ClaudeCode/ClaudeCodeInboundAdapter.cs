using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

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
        Log.Debug($"adapter {Name}: identity  model={clientBody.Model}  messages={clientBody.Messages.Count}  stream={clientBody.Stream == true}");
        return ValueTask.FromResult(clientBody);
    }

    /// <summary>
    /// Split the inbound <c>anthropic-beta</c> header into a case-insensitive
    /// set of tokens. Handles both CSV (<c>"foo, bar"</c>) and concatenated
    /// multi-header (ASP.NET joins repeated headers with comma in the captured
    /// string). Empty / missing → empty set.
    /// </summary>
    public static IReadOnlySet<string> ParseInboundBetas(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("anthropic-beta", out var raw) || string.IsNullOrWhiteSpace(raw))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(','))
        {
            var token = part.Trim();
            if (token.Length > 0) set.Add(token);
        }
        return set;
    }
}

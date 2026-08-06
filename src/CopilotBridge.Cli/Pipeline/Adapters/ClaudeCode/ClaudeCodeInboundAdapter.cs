using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Near-identity adapter — Claude Code speaks the Anthropic Messages API, which is
/// also the bridge's IR shape, so the inbound side is a passthrough EXCEPT for one
/// client-protocol concern: unfolding a reasoning carrier this same edge folded on
/// the way out.
/// <para>The Anthropic wire can carry provider reasoning state only as the single
/// opaque <c>redacted_thinking.data</c> string, so the outbound edge packed the whole
/// Responses item into it. Here it is unpacked back into the IR's part-level bag —
/// the shape every backend translator already pulls from. Nothing downstream needs
/// to know this envelope exists.</para>
/// </summary>
internal sealed class ClaudeCodeInboundAdapter : IClientInboundAdapter<MessagesRequest, MessagesRequest>
{
    private readonly ILogger<ClaudeCodeInboundAdapter> _log;

    public ClaudeCodeInboundAdapter(ILogger<ClaudeCodeInboundAdapter> log)
    {
        _log = log;
    }

    public string Name => "ClaudeCodeInbound";

    public ValueTask<MessagesRequest> AdaptAsync(
        MessagesRequest clientBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        var body = UnfoldReasoningCarriers(clientBody);
        _log.LogDebug(
            "adapter {Name}: identity  model={Model}  messages={Messages}  stream={Stream}",
            Name, body.Model, body.Messages.Count, body.Stream == true);
        return ValueTask.FromResult(body);
    }

    /// <summary>
    /// Replace every folded reasoning carrier with its unpacked form: <c>Data</c> back
    /// to the raw encrypted blob, the rest of the item onto the part bag. Requests
    /// without a carrier (every native Anthropic conversation) return the SAME instance,
    /// so the `/cc` hot path allocates nothing.
    /// </summary>
    private static MessagesRequest UnfoldReasoningCarriers(MessagesRequest body)
    {
        List<MessageParam>? rewrittenMessages = null;
        for (var i = 0; i < body.Messages.Count; i++)
        {
            var message = body.Messages[i];
            List<ContentBlockParam>? rewrittenBlocks = null;
            for (var j = 0; j < message.Content.Count; j++)
            {
                if (message.Content[j] is not RedactedThinkingBlockParam redacted
                    || !redacted.Data.StartsWith(ClaudeReasoningEnvelope.Prefix, StringComparison.Ordinal))
                    continue;

                var unfold = ClaudeReasoningEnvelope.TryUnfold(
                    redacted.Data, out var encryptedContent, out var bag);
                if (unfold == ClaudeReasoningUnfold.Absent) continue;
                if (unfold == ClaudeReasoningUnfold.Invalid)
                    throw new InvalidClaudeReasoningEnvelopeException();

                rewrittenBlocks ??= [.. message.Content];
                rewrittenBlocks[j] = redacted with
                {
                    Data = encryptedContent,
                    ProviderExtensions = bag,
                };
            }
            if (rewrittenBlocks is null) continue;

            rewrittenMessages ??= [.. body.Messages];
            rewrittenMessages[i] = message with { Content = rewrittenBlocks };
        }

        return rewrittenMessages is null ? body : body with { Messages = rewrittenMessages };
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

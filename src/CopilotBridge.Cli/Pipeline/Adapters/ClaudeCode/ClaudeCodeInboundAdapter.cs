using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Claude Code speaks the Anthropic Messages API, which is also the bridge's IR
/// shape, so this adapter is a passthrough apart from one thing: it undoes the
/// encoding its own outbound half applied.
/// </summary>
/// <remarks>
/// <para><see cref="ClaudeCodeOutboundAdapter"/> folds a reasoning item into the
/// single opaque string the Anthropic wire can carry, because that wire has no
/// other field for it. This adapter unfolds it back into the part-level provider
/// bag the IR carries. Both halves are the same edge codec — the fold has no
/// meaning outside this pair, so the unfold belongs here and nowhere else.</para>
/// <para>It needs no gate. It is not "restoring state for a Responses backend"
/// (that would be a destination concern, and knowing the destination here would
/// mean baking routing into a client edge); it is this edge decoding a value this
/// edge encoded. Only bytes carrying the private discriminator are touched, so a
/// provider-native blob stays opaque, and the Codex edge — which uses its own
/// adapter and never mints a carrier — cannot reach this code at all.</para>
/// </remarks>
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
        var body = UnfoldReasoningCarriers(clientBody, out var unfolded, out var droppedForeign);
        if (droppedForeign > 0)
        {
            // Visible on purpose: a silent drop looks identical to a model that just
            // stopped reasoning, and the cause (a routing/model change earlier in the
            // session) is nowhere near the turn where it shows up.
            _log.LogInformation(
                "adapter {Name}: dropped {Count} reasoning carrier(s) minted by a different model "
                + "— replaying another model's encrypted state is not valid; this turn runs without it",
                Name, droppedForeign);
        }
        _log.LogDebug(
            "adapter {Name}: model={Model}  messages={Messages}  stream={Stream}  reasoning_carriers={Carriers}",
            Name, body.Model, body.Messages.Count, body.Stream == true, unfolded);
        return ValueTask.FromResult(body);
    }

    /// <summary>
    /// Decode every carrier this edge previously minted back into the IR's provider
    /// bag. A body with none — every first turn, and every conversation whose
    /// assistant turns came from an Anthropic backend — is returned unchanged, so
    /// the common path allocates nothing.
    /// </summary>
    private static MessagesRequest UnfoldReasoningCarriers(
        MessagesRequest body, out int unfolded, out int droppedForeign)
    {
        unfolded = 0;
        droppedForeign = 0;
        List<MessageParam>? rewrittenMessages = null;

        for (var i = 0; i < body.Messages.Count; i++)
        {
            var message = body.Messages[i];
            List<ContentBlockParam>? rewrittenBlocks = null;

            for (var j = 0; j < message.Content.Count; j++)
            {
                if (message.Content[j] is not RedactedThinkingBlockParam redacted)
                    continue;

                var result = ClaudeReasoningEnvelope.TryUnfold(
                    redacted.Data, out var encryptedContent, out var bag, out var origin);
                if (result == ClaudeReasoningUnfold.Absent) continue;
                // The discriminator matched but the payload did not: this edge minted
                // it, so it is this edge's job to reject it rather than forward
                // arbitrary decoded JSON as if it were provider state.
                if (result == ClaudeReasoningUnfold.Invalid)
                    throw new InvalidClaudeReasoningEnvelopeException();

                rewrittenBlocks ??= [.. message.Content];

                // Encrypted reasoning state is private to the model that produced it.
                // After a mid-session model change the transcript still carries the
                // old model's carrier; replaying it would hand model B an opaque blob
                // only model A can read. Drop it and let the turn run stateless —
                // a missing reasoning item costs one turn's hidden state, a foreign
                // one is either a 400 or silently accepted nonsense. Not an error:
                // switching models is a legitimate thing for a user to do.
                if (origin is { Length: > 0 } && !string.Equals(origin, body.Model, StringComparison.OrdinalIgnoreCase))
                {
                    rewrittenBlocks[j] = redacted with { Data = "", ProviderExtensions = null };
                    droppedForeign++;
                    continue;
                }

                rewrittenBlocks[j] = redacted with
                {
                    Data = encryptedContent,
                    ProviderExtensions = bag,
                };
                unfolded++;
            }

            if (rewrittenBlocks is null) continue;
            rewrittenMessages ??= [.. body.Messages];
            rewrittenMessages[i] = message with
            {
                // A dropped carrier leaves no empty husk behind: remove the block
                // entirely so the outbound turn looks like one that never had it.
                Content = [.. rewrittenBlocks.Where(b =>
                    b is not RedactedThinkingBlockParam { Data.Length: 0 })],
            };
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

using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Drop a replayed reasoning carrier that was minted by a different model.
/// </summary>
/// <remarks>
/// <para>Encrypted reasoning state is private to the model that produced it, so a
/// carrier is only replayable back to its own origin. Deciding that needs the
/// RESOLVED target, which is why this is a stage and not part of the client edge:
/// the adapter runs before <see cref="ModelRouterStage"/>, where the body still
/// names the model the CLIENT asked for. On the CC→gpt route those differ by
/// design — the client says <c>claude-opus-5</c> and routing resolves
/// <c>gpt-5.6-sol</c> — so comparing at the edge would drop every valid carrier
/// and still miss a rewrite performed later.</para>
/// <para>The split keeps the layering intact. The client edge decodes what it
/// encoded and states the origin as a fact on the IR; this stage, which legitimately
/// knows the destination, pulls that fact and judges it. Neither half reaches into
/// the other's concern.</para>
/// <para>A mismatch DROPS the block rather than failing the request: switching model
/// mid-session is a legitimate user action, and a turn missing hidden state recovers
/// while a turn carrying another model's state does not.</para>
/// </remarks>
internal sealed class ClaudeReasoningOriginStage : IRequestStage<MessagesRequest>
{
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger<ClaudeReasoningOriginStage> _log;

    public ClaudeReasoningOriginStage(
        BridgeContext<MessagesRequest> ctx,
        ILogger<ClaudeReasoningOriginStage> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public string Name => "ClaudeReasoningOrigin";

    public Task ApplyAsync()
    {
        var ctx = _ctx;
        var body = ctx.Request.Body;
        var target = body.Model;
        List<MessageParam>? rewrittenMessages = null;
        var dropped = 0;

        for (var i = 0; i < body.Messages.Count; i++)
        {
            var message = body.Messages[i];
            List<ContentBlockParam>? rewrittenBlocks = null;

            for (var j = 0; j < message.Content.Count; j++)
            {
                if (message.Content[j] is not RedactedThinkingBlockParam redacted) continue;
                var origin = ClaudeReasoningEnvelope.ReadOrigin(redacted.ProviderExtensions);
                if (origin is not { Length: > 0 }) continue;

                rewrittenBlocks ??= [.. message.Content];
                if (string.Equals(origin, target, StringComparison.OrdinalIgnoreCase))
                {
                    // Same model: keep the state, but strip the bridge-private origin
                    // key so it cannot ride out onto the wire.
                    rewrittenBlocks[j] = redacted with
                    {
                        ProviderExtensions = StripOrigin(redacted.ProviderExtensions),
                    };
                    continue;
                }
                rewrittenBlocks[j] = null!;   // marked for removal below
                dropped++;
            }

            if (rewrittenBlocks is null) continue;
            rewrittenMessages ??= [.. body.Messages];
            rewrittenMessages[i] = message with
            {
                Content = [.. rewrittenBlocks.Where(b => b is not null)],
            };
        }

        if (rewrittenMessages is null) return Task.CompletedTask;
        ctx.Request.Body = body with { Messages = rewrittenMessages };

        if (dropped > 0)
        {
            ctx.Response.ForeignReasoningCarriersDropped = dropped;
            // Visible on purpose: a silent drop is indistinguishable from a model
            // that simply stopped reasoning, and the cause — a model change earlier
            // in the session — is nowhere near the turn where it surfaces.
            _log.LogInformation(
                "stage {Name}: dropped {Count} reasoning carrier(s) minted by another model; "
                + "target={Target}. Replaying another model's encrypted state is not valid, "
                + "so this turn runs without it",
                Name, dropped, target);
        }
        return Task.CompletedTask;
    }

    private static ProviderExtensions? StripOrigin(ProviderExtensions? bag)
    {
        if (bag is null
            || !bag.ByProvider.TryGetValue(
                Adapters.Codex.ResponsesToIrInboundAdapter.OpenAiProviderKey, out var inner)
            || inner.ValueKind != JsonValueKind.Object
            || !inner.TryGetProperty(ClaudeReasoningEnvelope.OriginBagKey, out _))
            return bag;

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var p in inner.EnumerateObject())
            {
                if (p.NameEquals(ClaudeReasoningEnvelope.OriginBagKey)) continue;
                p.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement>
            {
                [Adapters.Codex.ResponsesToIrInboundAdapter.OpenAiProviderKey] =
                    doc.RootElement.Clone(),
            },
        };
    }
}

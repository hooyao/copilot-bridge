using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Unfold a bridge reasoning carrier a Claude client echoed back, turning the single
/// opaque <c>redacted_thinking.data</c> string the Anthropic wire can carry into the
/// part-level provider bag every backend translator already pulls from.
/// </summary>
/// <remarks>
/// <para>Runs as a request STAGE rather than in the inbound adapter because the
/// adapter executes before the model router: only after target selection is it known
/// that a Responses backend will serve this request. A native Anthropic passthrough
/// must never have its opaque provider data reinterpreted, so this stage no-ops unless
/// <see cref="RouteTarget.Vendor"/> is <see cref="BackendVendor.CopilotResponses"/>.</para>
/// <para>A body with no carrier — every native Anthropic conversation, and every first
/// turn — leaves the IR instance untouched, so the hot path allocates nothing.</para>
/// </remarks>
internal sealed class ClaudeReasoningUnfoldStage : IRequestStage<MessagesRequest>
{
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger<ClaudeReasoningUnfoldStage> _log;

    public ClaudeReasoningUnfoldStage(
        BridgeContext<MessagesRequest> ctx,
        ILogger<ClaudeReasoningUnfoldStage> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public string Name => "ClaudeReasoningUnfold";

    public Task ApplyAsync()
    {
        var ctx = _ctx;
        // Only a Responses backend can consume a restored reasoning item, and only a
        // Claude client edge ever produced this carrier.
        if (ctx.Target?.Vendor != BackendVendor.CopilotResponses)
            return Task.CompletedTask;

        var body = ctx.Request.Body;
        List<MessageParam>? rewrittenMessages = null;
        var unfolded = 0;

        for (var i = 0; i < body.Messages.Count; i++)
        {
            var message = body.Messages[i];
            List<ContentBlockParam>? rewrittenBlocks = null;

            for (var j = 0; j < message.Content.Count; j++)
            {
                if (message.Content[j] is not RedactedThinkingBlockParam redacted)
                    continue;

                var result = ClaudeReasoningEnvelope.TryUnfold(
                    redacted.Data, out var encryptedContent, out var bag);
                if (result == ClaudeReasoningUnfold.Absent) continue;
                if (result == ClaudeReasoningUnfold.Invalid)
                    throw new InvalidClaudeReasoningEnvelopeException();

                rewrittenBlocks ??= [.. message.Content];
                rewrittenBlocks[j] = redacted with
                {
                    Data = encryptedContent,
                    ProviderExtensions = bag,
                };
                unfolded++;
            }

            if (rewrittenBlocks is null) continue;
            rewrittenMessages ??= [.. body.Messages];
            rewrittenMessages[i] = message with { Content = rewrittenBlocks };
        }

        if (rewrittenMessages is null) return Task.CompletedTask;

        ctx.Request.Body = body with { Messages = rewrittenMessages };
        _log.LogDebug("stage {Name}: restored {Count} reasoning item(s) from client carriers",
            Name, unfolded);
        return Task.CompletedTask;
    }
}

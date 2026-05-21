using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Drops invalid <c>thinking</c> blocks from historical assistant messages.
/// Per research §3.6 rule 2, a thinking block is only useful upstream when
/// both fields are populated and the signature is the real signed form (not
/// the unsigned <c>"@"</c>-prefixed placeholder Claude Code injects when it
/// doesn't have the signed version). Sending unsigned blocks back to Copilot
/// either produces no value (re-thought from scratch) or 400s.
/// </summary>
internal sealed class AssistantThinkingFilterStage : IRequestStage<MessagesRequest>
{
    public string Name => "AssistantThinkingFilter";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var dropped = 0;
        var anyChanged = false;
        var newMessages = new List<MessageParam>(ctx.Request.Body.Messages.Count);
        foreach (var msg in ctx.Request.Body.Messages)
        {
            if (msg.Role != Role.Assistant)
            {
                newMessages.Add(msg);
                continue;
            }
            var newContent = new List<ContentBlockParam>(msg.Content.Count);
            var msgChanged = false;
            foreach (var block in msg.Content)
            {
                if (block is ThinkingBlockParam t && !IsValidSignedThinking(t))
                {
                    dropped++;
                    msgChanged = true;
                    continue;
                }
                newContent.Add(block);
            }
            if (msgChanged)
            {
                newMessages.Add(msg with { Content = newContent });
                anyChanged = true;
            }
            else
            {
                newMessages.Add(msg);
            }
        }

        if (anyChanged)
        {
            ctx.Request.Body = ctx.Request.Body with { Messages = newMessages };
        }

        Log.Debug($"stage {Name}: dropped {dropped} unsigned/placeholder thinking blocks");
        return Task.CompletedTask;
    }

    private static bool IsValidSignedThinking(ThinkingBlockParam t)
    {
        if (string.IsNullOrWhiteSpace(t.Thinking)) return false;
        if (string.IsNullOrEmpty(t.Signature)) return false;
        // Claude Code injects literal "@..." as a placeholder signature for
        // turns it has no signed thinking for. Detect via '@' presence.
        if (t.Signature.Contains('@')) return false;
        return true;
    }
}

using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

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
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger<AssistantThinkingFilterStage> _log;

    public AssistantThinkingFilterStage(
        BridgeContext<MessagesRequest> ctx,
        ILogger<AssistantThinkingFilterStage> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public string Name => "AssistantThinkingFilter";

    public Task ApplyAsync()
    {
        var ctx = _ctx;
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

        _log.LogDebug("stage {Name}: dropped {Dropped} unsigned/placeholder thinking blocks", Name, dropped);
        return Task.CompletedTask;
    }

    private static bool IsValidSignedThinking(ThinkingBlockParam t)
    {
        if (string.IsNullOrWhiteSpace(t.Thinking)) return false;
        if (string.IsNullOrEmpty(t.Signature)) return false;
        if (t.Signature.Contains('@')) return false;
        return true;
    }
}

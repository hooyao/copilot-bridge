using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Two cleanups on the messages array:
/// <list type="number">
///   <item>Drops standalone <c>"Tool loaded."</c> text blocks — Claude Code's
///         tool-reference turn boundary marker that Copilot does not need
///         (research §3.6 rule 8).</item>
///   <item>Appends a <c>{role:"user", content:"Please continue."}</c> message
///         when the last existing message is from the assistant. Anthropic's
///         API accepts trailing-assistant as a prefill, but Copilot's
///         translation layer 400s on it (research §3.6 rule 3).</item>
/// </list>
/// </summary>
internal sealed class MessagesSanitizeStage : IRequestStage<MessagesRequest>
{
    private const string ToolLoadedMarker = "Tool loaded.";
    private const string ContinuePrompt = "Please continue.";

    private readonly ILogger<MessagesSanitizeStage> _log;

    public MessagesSanitizeStage(ILogger<MessagesSanitizeStage> log)
    {
        _log = log;
    }

    public string Name => "MessagesSanitize";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var droppedToolLoaded = 0;
        var newMessages = new List<MessageParam>(ctx.Request.Body.Messages.Count);
        foreach (var msg in ctx.Request.Body.Messages)
        {
            var rebuilt = new List<ContentBlockParam>(msg.Content.Count);
            foreach (var block in msg.Content)
            {
                if (block is TextBlockParam text && IsToolLoadedMarker(text.Text))
                {
                    droppedToolLoaded++;
                    continue;
                }
                rebuilt.Add(block);
            }
            newMessages.Add(rebuilt.Count == msg.Content.Count
                ? msg
                : msg with { Content = rebuilt });
        }

        var trailingAssistantFixed = false;
        if (newMessages.Count > 0 && newMessages[^1].Role == Role.Assistant)
        {
            newMessages.Add(new MessageParam
            {
                Role = Role.User,
                Content = [new TextBlockParam { Text = ContinuePrompt }],
            });
            trailingAssistantFixed = true;
        }

        if (droppedToolLoaded > 0 || trailingAssistantFixed)
        {
            ctx.Request.Body = ctx.Request.Body with { Messages = newMessages };
        }

        _log.LogDebug(
            "stage {Name}: dropped \"Tool loaded.\" x{DroppedToolLoaded}  trailing-assistant-fix={TrailingAssistantFixed}",
            Name, droppedToolLoaded, trailingAssistantFixed);
        return Task.CompletedTask;
    }

    private static bool IsToolLoadedMarker(string text)
    {
        var span = text.AsSpan().Trim();
        return span.SequenceEqual(ToolLoadedMarker);
    }
}

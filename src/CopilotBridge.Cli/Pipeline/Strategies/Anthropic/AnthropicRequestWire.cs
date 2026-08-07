using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Strategies.Anthropic;

/// <summary>
/// Projects the semantic IR onto the Anthropic wire. Provider extensions are
/// internal source-pushed data; this destination consumes no provider namespace,
/// so none may serialize. The common Claude path returns the original object.
/// </summary>
internal static class AnthropicRequestWire
{
    public static MessagesRequest Project(MessagesRequest request)
    {
        if (!HasProviderExtensions(request)) return request;

        return request with
        {
            ProviderExtensions = null,
            System = request.System?.Select(Strip).ToArray(),
            Messages = request.Messages.Select(message => message with
            {
                ProviderExtensions = null,
                Content = message.Content.Select(Strip).ToArray(),
            }).ToArray(),
        };
    }

    private static bool HasProviderExtensions(MessagesRequest request) =>
        request.ProviderExtensions is not null
        || request.System?.Any(part => part.ProviderExtensions is not null) == true
        || request.Messages.Any(message =>
            message.ProviderExtensions is not null
            || message.Content.Any(part => part.ProviderExtensions is not null));

    private static TextBlockParam Strip(TextBlockParam part) =>
        part.ProviderExtensions is null ? part : part with { ProviderExtensions = null };

    private static ContentBlockParam Strip(ContentBlockParam part)
    {
        if (part.ProviderExtensions is null) return part;
        return part switch
        {
            TextBlockParam value => value with { ProviderExtensions = null },
            ImageBlockParam value => value with { ProviderExtensions = null },
            DocumentBlockParam value => value with { ProviderExtensions = null },
            ToolUseBlockParam value => value with { ProviderExtensions = null },
            ToolResultBlockParam value => value with { ProviderExtensions = null },
            ThinkingBlockParam value => value with { ProviderExtensions = null },
            RedactedThinkingBlockParam value => value with { ProviderExtensions = null },
            _ => part,
        };
    }
}

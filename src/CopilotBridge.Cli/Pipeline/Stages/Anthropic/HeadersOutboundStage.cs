using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Final request stage. Discards the inbound header set and rebuilds it as the
/// outbound (Copilot-bound) header set, mirroring
/// <c>references/vscode-copilot-chat-snippets/chatEndpoint.ts:193-210</c>:
/// <code>
/// const betaFeatures: string[] = [];
/// if (!this.supportsAdaptiveThinking)               betaFeatures.push('interleaved-thinking-2025-05-14');
/// if (isAnthropicContextEditingEnabled(...))        betaFeatures.push('context-management-2025-06-27');
/// if (isAnthropicToolSearchEnabled(...))            betaFeatures.push('advanced-tool-use-2025-11-20');
/// if (betaFeatures.length > 0) headers['anthropic-beta'] = betaFeatures.join(',');
/// </code>
/// Plus a <c>copilot-vision-request</c> flag when the body has image content.
/// The static 7-header set + Authorization land later via
/// <see cref="Copilot.CopilotHeaderFactory"/> inside the strategy's HTTP call.
/// </summary>
/// <remarks>
/// MUST run last in the request pipeline — earlier stages may want to read
/// inbound headers; this stage clears them.
/// </remarks>
internal sealed class HeadersOutboundStage : IRequestStage<MessagesRequest>
{
    public string Name => "HeadersOutbound";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        ctx.Request.Headers.Clear();

        var betas = new List<string>(3);

        // chatEndpoint.ts: `if (!this.supportsAdaptiveThinking)` — push when
        // the model lacks adaptive thinking support. Per Copilot's /models
        // metadata, all current Claude variants advertise support; haiku-4.5
        // lies (research §15.3) but we mirror the official client's trust.
        if (!ModelSupportsAdaptiveThinking(ctx.Target!.ModelId))
        {
            betas.Add("interleaved-thinking-2025-05-14");
        }

        // chatEndpoint.ts: `if (isAnthropicContextEditingEnabled(...))` — gated
        // on a config flag in VS Code. Locally we use the simpler proxy "the
        // request body actually has a context_management field" — there's no
        // point sending the beta otherwise.
        if (ctx.Request.Body.ContextManagement is not null)
        {
            betas.Add("context-management-2025-06-27");
        }

        // chatEndpoint.ts: `if (isAnthropicToolSearchEnabled(...))` — gated on
        // a config flag in VS Code. M1 doesn't model server-side tool_search
        // tool variants in the request DTO, so this is always false for now.
        if (HasToolSearchTools(ctx.Request.Body))
        {
            betas.Add("advanced-tool-use-2025-11-20");
        }

        if (betas.Count > 0)
        {
            ctx.Request.Headers["anthropic-beta"] = string.Join(',', betas);
        }

        if (HasImageContent(ctx.Request.Body))
        {
            ctx.Request.Headers["copilot-vision-request"] = "true";
        }

        Log.Debug($"stage {Name}: betas=[{string.Join(',', betas)}] vision={ctx.Request.Headers.ContainsKey("copilot-vision-request")}");
        return Task.CompletedTask;
    }

    private static bool ModelSupportsAdaptiveThinking(string modelId)
    {
        // M1: trust Copilot's metadata (which says all current Claude models
        // support adaptive). When we add older Claude families that genuinely
        // need explicit thinking, surface a fact in CopilotModelRegistry and
        // consult it here.
        return true;
    }

    private static bool HasImageContent(MessagesRequest req)
    {
        foreach (var msg in req.Messages)
        {
            foreach (var block in msg.Content)
            {
                if (block is ImageBlockParam) return true;
            }
        }
        return false;
    }

    private static bool HasToolSearchTools(MessagesRequest req)
    {
        // M1's Tool DTO only models the custom-tool variant; server-side
        // tool_search tools (BetaToolSearchToolBm25_20251119,
        // BetaToolSearchToolRegex20251119) aren't typed yet, so we never
        // detect them. Wire this when those variants are added to ContentBlock /
        // tool unions.
        _ = req;
        return false;
    }
}

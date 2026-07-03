using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

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
/// <para>MUST run last in the request pipeline — earlier stages may want to read
/// inbound headers; this stage clears them.</para>
/// <para>Beta forwarding policy: <b>pass-through-by-default</b>. The final beta
/// set is the union of (a) the three derived tokens above and (b) every token
/// in <c>ctx.InboundBetas</c>, then minus everything matched by
/// <c>ctx.PendingBetaStrips</c>. Rationale + empirical data in
/// <c>docs/pipeline-design.md §7.5</c>.</para>
/// </remarks>
internal sealed class HeadersOutboundStage : IRequestStage<MessagesRequest>
{
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger<HeadersOutboundStage> _log;

    public HeadersOutboundStage(
        BridgeContext<MessagesRequest> ctx,
        ILogger<HeadersOutboundStage> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public string Name => "HeadersOutbound";

    public Task ApplyAsync()
    {
        var ctx = _ctx;
        ctx.Request.Headers.Clear();

        var betas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!ModelSupportsAdaptiveThinking(ctx.Target!.ModelId))
        {
            betas.Add("interleaved-thinking-2025-05-14");
        }

        if (ctx.Request.Body.ContextManagement is not null)
        {
            betas.Add("context-management-2025-06-27");
        }

        if (HasToolSearchTools(ctx.Request.Body))
        {
            betas.Add("advanced-tool-use-2025-11-20");
        }

        // Pass-through: union inbound betas onto the derived set.
        foreach (var token in ctx.InboundBetas)
        {
            betas.Add(token);
        }

        // Add tokens declared by routing locations via Use.Headers.Set on
        // "anthropic-beta". Done before strips so an Add immediately followed
        // by a wildcard Strip (e.g. add 'foo-2026' then strip 'foo-*') still
        // produces the expected end state: nothing.
        foreach (var token in ctx.PendingBetaAdds)
        {
            betas.Add(token);
        }

        if (ctx.PendingBetaStrips.Count > 0)
        {
            betas.RemoveWhere(token => MatchesAnyStripPattern(token, ctx.PendingBetaStrips));
        }

        if (betas.Count > 0)
        {
            ctx.Request.Headers["anthropic-beta"] = string.Join(',', betas);
        }

        if (HasImageContent(ctx.Request.Body))
        {
            ctx.Request.Headers["copilot-vision-request"] = "true";
        }

        _log.LogDebug(
            "stage {Name}: betas=[{Betas}] adds=[{Adds}] strips=[{Strips}] vision={Vision}",
            Name,
            string.Join(',', betas),
            string.Join(',', ctx.PendingBetaAdds),
            string.Join(',', ctx.PendingBetaStrips),
            ctx.Request.Headers.ContainsKey("copilot-vision-request"));
        return Task.CompletedTask;
    }

    private static bool MatchesAnyStripPattern(string token, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else
            {
                if (token.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static bool ModelSupportsAdaptiveThinking(string modelId)
    {
        // M1: trust Copilot's metadata (which says all current Claude models
        // support adaptive). When we add older Claude families that genuinely
        // need explicit thinking, surface a fact in CopilotModelRegistry and
        // consult it here.
        _ = modelId;
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

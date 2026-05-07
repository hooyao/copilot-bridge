using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Assembly point for the bridge's pipelines. Builds one
/// <see cref="Pipeline{TBody}"/> per (client, IR) pair; M1 has just one — the
/// IR-shape pipeline that all Anthropic-shape clients (currently only Claude
/// Code) flow through.
/// </summary>
internal static class BridgePipelines
{
    public static Pipeline<MessagesRequest> BuildAnthropic(
        IModelRegistry models,
        ICopilotClient copilot)
    {
        return new Pipeline<MessagesRequest>
        {
            Name = "Anthropic-IR",
            RequestStages =
            [
                // 1. Routing first — every later stage that needs ctx.Target reads it here.
                new ModelRouterStage(models),

                // 2-5. Body-level cleanups in any order; each is independent.
                //
                //  Note: CacheControlCleanStage from research §3.6 rule 1 is intentionally
                //  not present. The DTO does not model `cache_control.scope`, so the field
                //  is silently dropped at deserialize time and never reaches Copilot. If
                //  the DTO ever grows a Scope property, add the stage to actively clear it.
                new AssistantThinkingFilterStage(),
                new SystemSanitizeStage(),
                new MessagesSanitizeStage(),
                new ToolsSanitizeStage(),

                // 6. Per-target-family thinking shape — needs ctx.Target (set in step 1).
                new ThinkingRewriteStage(),

                // 7. Always last — generates outbound headers from the FINAL body shape.
                new HeadersOutboundStage(),
            ],
            ResponseStages =
            [
                new DoneFilterStage(),
            ],
            Strategies = new StrategyRegistry<MessagesRequest>(
            [
                new CopilotMessagesPassthroughStrategy(copilot),
            ]),
        };
    }
}

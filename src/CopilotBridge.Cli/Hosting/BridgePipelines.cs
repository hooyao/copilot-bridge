using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using Microsoft.Extensions.Options;

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
        ModelProfileCatalog profiles,
        ICopilotClient copilot,
        IOptions<RoutesConfig> routesOptions)
    {
        return new Pipeline<MessagesRequest>
        {
            Name = "Anthropic-IR",
            RequestStages =
            [
                // 1. Routing + per-model body coercion. Normalize → first
                //    matching user rule (model redirect only) → profile
                //    lookup in ModelProfileCatalog → ProfileAdjuster
                //    mechanically shapes the body to what the target
                //    profile accepts. A missing profile throws
                //    UnknownModelException, surfaced as a 400 by the
                //    endpoint.
                new ModelRouterStage(models, profiles, routesOptions),

                // 2-5. Body-level cleanups, each independent of model family.
                //
                //  Note: CacheControlCleanStage from research §3.6 rule 1 is
                //  intentionally not present. The DTO does not model
                //  `cache_control.scope`, so the field is silently dropped at
                //  deserialize time. If the DTO ever grows a Scope property,
                //  add the stage to actively clear it.
                new AssistantThinkingFilterStage(),
                new SystemSanitizeStage(),
                new MessagesSanitizeStage(),
                new ToolsSanitizeStage(),

                // 6. Always last — generates outbound headers from the FINAL body shape.
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

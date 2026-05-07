using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Rewrites <c>thinking</c> per the model family's actual API shape, since
/// Copilot's <c>capabilities.supports.adaptive_thinking</c> flag is unreliable
/// for some families (research §15.3):
/// <list type="bullet">
///   <item><c>claude-haiku-*</c> → only accepts
///         <c>thinking:{type:"enabled", budget_tokens:N}</c>; adaptive 400s.</item>
///   <item><c>claude-opus-4.7*</c> → only accepts
///         <c>thinking:{type:"adaptive"}</c> + <c>output_config:{effort:X}</c>;
///         explicit 400s.</item>
///   <item><c>claude-sonnet-*</c> (and everything else) → both shapes work,
///         passthrough.</item>
/// </list>
/// Conversion direction:
/// <list type="bullet">
///   <item>haiku family receiving adaptive → downgrade to enabled with budget
///         derived from the <see cref="OutputConfig.Effort"/> value (default
///         <c>medium</c> = 16384).</item>
///   <item>opus-4.7 family receiving enabled → upgrade to adaptive with
///         <c>output_config.effort</c> derived from <c>budget_tokens</c>.</item>
/// </list>
/// </summary>
internal sealed class ThinkingRewriteStage : IRequestStage<MessagesRequest>
{
    public string Name => "ThinkingRewrite";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var modelId = ctx.Target!.ModelId;
        var thinking = ctx.Request.Body.Thinking;
        if (thinking is null) return Task.CompletedTask;

        if (IsHaikuFamily(modelId))
        {
            if (thinking is ThinkingConfigAdaptive adaptive)
            {
                var budget = EffortToBudget(ctx.Request.Body.OutputConfig?.Effort);
                ctx.Request.Body = ctx.Request.Body with
                {
                    Thinking = new ThinkingConfigEnabled
                    {
                        BudgetTokens = budget,
                        Display = adaptive.Display,
                    },
                    // OutputConfig.effort is meaningless once we're on enabled+budget;
                    // strip it to avoid Copilot complaining about extra fields.
                    OutputConfig = ctx.Request.Body.OutputConfig is { } existing
                        ? existing with { Effort = null }
                        : null,
                };
                DiagTracer.Log($"stage {Name}: haiku — adaptive → enabled budget={budget} (from effort={ctx.Request.Body.OutputConfig?.Effort ?? "default"})");
            }
        }
        else if (IsOpus47Family(modelId))
        {
            if (thinking is ThinkingConfigEnabled enabled)
            {
                var effort = BudgetToEffort(enabled.BudgetTokens);
                var newOutputConfig = (ctx.Request.Body.OutputConfig ?? new OutputConfig()) with { Effort = effort };
                ctx.Request.Body = ctx.Request.Body with
                {
                    Thinking = new ThinkingConfigAdaptive { Display = enabled.Display },
                    OutputConfig = newOutputConfig,
                };
                DiagTracer.Log($"stage {Name}: opus-4.7 — enabled budget={enabled.BudgetTokens} → adaptive + effort={effort}");
            }
        }
        else
        {
            DiagTracer.Log($"stage {Name}: {modelId} — passthrough");
        }
        return Task.CompletedTask;
    }

    private static bool IsHaikuFamily(string modelId) =>
        modelId.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpus47Family(string modelId) =>
        modelId.StartsWith("claude-opus-4.7", StringComparison.OrdinalIgnoreCase);

    private static int EffortToBudget(string? effort) => effort switch
    {
        "low" => 4096,
        "medium" => 16384,
        "high" => 32768,
        "xhigh" => 64000,
        "max" => 64000,
        _ => 16384, // sensible default ≈ medium
    };

    private static string BudgetToEffort(int budgetTokens) => budgetTokens switch
    {
        < 8192 => "low",
        < 24000 => "medium",
        < 48000 => "high",
        _ => "xhigh",
    };
}

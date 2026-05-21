using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Two-layer routing resolver:
/// <list type="number">
///   <item>Walk <see cref="RoutesConfig.Rules"/> once, first-match-wins.
///         A matching rule's <see cref="RuleRewrite"/> applies any of
///         model / effort / thinking. If the rule sets effort, it claims
///         that field — the capability layer below will not re-touch it.</item>
///   <item>Hand the (possibly-rewritten) body to
///         <see cref="IModelRegistry.ApplyEffortRouting"/> for the
///         capability layer: variant-suffix routing (a physical fact —
///         always applied) and effort-field strip on un-routed models.
///         Skipped entirely when the user rule already set effort.</item>
/// </list>
/// No iteration / no fixed-point — debugging stays trivial.
/// </summary>
internal static class ModelRouteResolver
{
    /// <summary>
    /// Apply user rules then capability to <paramref name="ctx"/> in
    /// place. Returns the rule that fired (for diag log) — null if no
    /// user rule matched.
    /// </summary>
    public static RoutingRule? Apply(
        BridgeContext<MessagesRequest> ctx, RoutesConfig config, IModelRegistry registry)
    {
        var (matched, effortExplicit) = ApplyUserRule(ctx, config.Rules);
        if (!effortExplicit)
        {
            ApplyCapability(ctx, registry);
        }
        return matched;
    }

    private static (RoutingRule? Matched, bool EffortExplicit) ApplyUserRule(
        BridgeContext<MessagesRequest> ctx, List<RoutingRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!Matches(rule.Match, ctx.Request.Body)) continue;
            var effortExplicit = ApplyRewrite(ctx, rule.Rewrite);
            Log.Debug($"  routes/rule: matched [{rule.Note ?? Describe(rule)}]");
            return (rule, effortExplicit);
        }
        return (null, false);
    }

    private static bool Matches(RouteMatch match, MessagesRequest body)
    {
        if (match.InboundModel is not null
            && !string.Equals(match.InboundModel, body.Model, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (match.InboundEffort is not null)
        {
            var effort = body.OutputConfig?.Effort;
            if (!string.Equals(match.InboundEffort, effort, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (match.InboundThinking is not null)
        {
            var actual = ThinkingShape(body.Thinking);
            if (!string.Equals(match.InboundThinking, actual, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool ApplyRewrite(BridgeContext<MessagesRequest> ctx, RuleRewrite? r)
    {
        if (r is null) return false;

        var body = ctx.Request.Body;
        var effortExplicit = false;

        if (r.Model is { Length: > 0 } newModel)
        {
            body = body with { Model = newModel };
        }

        if (r.Effort is { Length: > 0 } newEffort)
        {
            var oc = (body.OutputConfig ?? new OutputConfig()) with { Effort = newEffort };
            body = body with { OutputConfig = oc };
            effortExplicit = true;
        }

        if (r.Thinking?.Type is { Length: > 0 } thinkingType)
        {
            body = body with { Thinking = BuildThinking(thinkingType, r.Thinking.BudgetTokens) };
        }

        if (r.DeriveBudgetFromEffort && body.Thinking is ThinkingConfigEnabled enabled)
        {
            var newBudget = EffortToBudget(body.OutputConfig?.Effort);
            body = body with { Thinking = enabled with { BudgetTokens = newBudget } };
        }

        if (r.DeriveEffortFromBudget && body.Thinking is ThinkingConfigEnabled withBudget)
        {
            var derivedEffort = BudgetToEffort(withBudget.BudgetTokens);
            var oc = (body.OutputConfig ?? new OutputConfig()) with { Effort = derivedEffort };
            body = body with { OutputConfig = oc };
            effortExplicit = true;
        }

        ctx.Request.Body = body;
        return effortExplicit;
    }

    private static ThinkingConfig BuildThinking(string type, int? budgetTokens) =>
        type.ToLowerInvariant() switch
        {
            "enabled"  => new ThinkingConfigEnabled { BudgetTokens = budgetTokens ?? 16384 },
            "adaptive" => new ThinkingConfigAdaptive(),
            "disabled" => new ThinkingConfigDisabled(),
            _ => throw new InvalidOperationException(
                $"Rewrite.Thinking.Type='{type}' is not one of: enabled, adaptive, disabled."),
        };

    private static void ApplyCapability(BridgeContext<MessagesRequest> ctx, IModelRegistry registry)
    {
        var body = ctx.Request.Body;
        var (newModel, stripEffort) = registry.ApplyEffortRouting(body.Model, body.OutputConfig?.Effort);

        var changed = false;
        if (!string.Equals(body.Model, newModel, StringComparison.OrdinalIgnoreCase))
        {
            body = body with { Model = newModel };
            changed = true;
        }
        if (stripEffort && body.OutputConfig?.Effort is not null)
        {
            body = body with { OutputConfig = body.OutputConfig with { Effort = null } };
            changed = true;
        }
        if (changed)
        {
            ctx.Request.Body = body;
            Log.Debug($"  routes/capability: model='{newModel}' stripEffort={stripEffort}");
        }
    }

    private static string? ThinkingShape(ThinkingConfig? thinking) => thinking switch
    {
        ThinkingConfigEnabled  => "enabled",
        ThinkingConfigAdaptive => "adaptive",
        ThinkingConfigDisabled => "disabled",
        _                      => null,
    };

    private static int EffortToBudget(string? effort) => effort switch
    {
        "low"    => 4096,
        "medium" => 16384,
        "high"   => 32768,
        "xhigh"  => 64000,
        "max"    => 64000,
        _        => 16384,
    };

    private static string BudgetToEffort(int budget) => budget switch
    {
        < 8192  => "low",
        < 24000 => "medium",
        < 48000 => "high",
        _       => "xhigh",
    };

    private static string Describe(RoutingRule r)
    {
        var parts = new List<string>();
        if (r.Match.InboundModel is not null) parts.Add($"model={r.Match.InboundModel}");
        if (r.Match.InboundEffort is not null) parts.Add($"effort={r.Match.InboundEffort}");
        if (r.Match.InboundThinking is not null) parts.Add($"thinking={r.Match.InboundThinking}");
        return string.Join(',', parts);
    }
}

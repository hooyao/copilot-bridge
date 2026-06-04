using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Mechanically reshapes the inbound <see cref="MessagesRequest"/> to match
/// what the resolved target <see cref="ModelProfile"/> accepts on the wire.
/// Pure function over (body, profile) → (body, possibly-new-target-model).
/// </summary>
internal static class ProfileAdjuster
{
    /// <summary>
    /// Adjust <paramref name="ctx"/>'s body in place to match
    /// <paramref name="profile"/>. Returns the profile of the model the body
    /// ends up addressing — usually the same one passed in, but variant-routing
    /// can switch profiles mid-adjust.
    /// </summary>
    /// <param name="globalBetaStrips">Operator-configured patterns appended
    /// to <c>ctx.PendingBetaStrips</c> before the per-profile strips. Comes
    /// from <see cref="Hosting.Options.OutboundBetaPolicyOptions"/> and
    /// defaults to <c>["advisor-tool-*", "structured-outputs-*"]</c> in the
    /// shipped <c>appsettings.json</c>; empty when this argument is null.</param>
    public static ModelProfile Apply(
        BridgeContext<MessagesRequest> ctx,
        ModelProfile profile,
        ModelProfileCatalog catalog,
        ILogger<ProfileAdjusterLog>? log = null,
        IReadOnlyList<string>? globalBetaStrips = null)
    {
        profile = ApplyEffort(ctx, profile, catalog, log);
        ApplyThinking(ctx, profile, log);

        // ApplyThinking can DERIVE output_config.effort from the thinking budget
        // (ThinkingPolicy.DeriveEffortFromBudgetOnCoerce) when it coerces an
        // adaptive-only model's thinking:enabled → adaptive — and that derived
        // value never passed effort validation. Re-run ApplyEffort so the derived
        // effort is validated against the (possibly variant-switched) target the
        // same way a client-sent one is: kept if accepted, else stripped or routed
        // to a -high/-xhigh sibling. Without this, Claude Code sending
        // thinking:enabled with a budget that derives to 'high'/'xhigh' makes
        // opus-4.8 and opus-4.7 base (both accept only 'medium') 400 with
        // "output_config.effort 'high' is not supported by model …; supported
        // values: [medium]" (verified live: opus-4.8 enabled+budget=32000 → 400).
        // Idempotent for every other path: models that don't derive an effort
        // leave the field untouched, so the second pass is a no-op for them.
        profile = ApplyEffort(ctx, profile, catalog, log);

        if (!profile.AcceptsMidConversationSystem)
        {
            FoldMidConversationSystem(ctx, log);
        }
        CapThinkingBudget(ctx, profile, log);

        // Step 5: register beta-strip patterns for HeadersOutboundStage.
        // Operator-configured global strips run first (these are tokens
        // Copilot's gateway rejects on EVERY model — they live in
        // appsettings.json so each deployment can tune what its backend
        // tolerates without a code change). Per-profile strips run after.
        if (globalBetaStrips is { Count: > 0 })
        {
            ctx.PendingBetaStrips.AddRange(globalBetaStrips);
        }
        if (profile.StripBetas.Count > 0)
        {
            ctx.PendingBetaStrips.AddRange(profile.StripBetas);
        }

        return profile;
    }

    private static ModelProfile ApplyEffort(
        BridgeContext<MessagesRequest> ctx, ModelProfile profile, ModelProfileCatalog catalog, ILogger? log)
    {
        var body = ctx.Request.Body;
        var effort = body.OutputConfig?.Effort;
        if (effort is null) return profile;

        foreach (var ok in profile.AcceptedEfforts)
        {
            if (string.Equals(ok, effort, StringComparison.OrdinalIgnoreCase)) return profile;
        }

        switch (profile.EffortOnUnsupported)
        {
            case EffortHandling.RouteToVariant:
                if (profile.EffortToVariant.TryGetValue(effort, out var variantId)
                 && catalog.Get(variantId) is { } variantProfile)
                {
                    body = body with
                    {
                        Model = variantId,
                        OutputConfig = body.OutputConfig is { } ocVariant ? ocVariant with { Effort = null } : null,
                    };
                    ctx.Request.Body = body;
                    log?.LogDebug(
                        "  profile/effort: '{Profile}' + effort='{Effort}' → variant '{Variant}' (effort stripped)",
                        profile.CanonicalId, effort, variantId);
                    return variantProfile;
                }
                goto case EffortHandling.Strip;

            case EffortHandling.Strip:
            default:
                body = body with { OutputConfig = body.OutputConfig is { } ocStrip ? ocStrip with { Effort = null } : null };
                ctx.Request.Body = body;
                log?.LogDebug(
                    "  profile/effort: '{Profile}' rejects effort='{Effort}' → stripped",
                    profile.CanonicalId, effort);
                return profile;
        }
    }

    private static void ApplyThinking(BridgeContext<MessagesRequest> ctx, ModelProfile profile, ILogger? log)
    {
        var body = ctx.Request.Body;
        var shape = ThinkingShape(body.Thinking);
        if (shape is null) return;

        var policy = profile.Thinking;
        foreach (var ok in policy.AcceptedShapes)
        {
            if (string.Equals(ok, shape, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(shape, "enabled", StringComparison.OrdinalIgnoreCase)
                    && body.Thinking is ThinkingConfigEnabled cur
                    && cur.BudgetTokens <= 0
                    && policy.DeriveBudgetFromEffortOnEnabled)
                {
                    body = body with { Thinking = cur with { BudgetTokens = EffortToBudget(body.OutputConfig?.Effort) } };
                    ctx.Request.Body = body;
                }
                return;
            }
        }

        var coerceTo = policy.CoerceToWhenUnsupported;

        if (policy.DeriveEffortFromBudgetOnCoerce
            && body.Thinking is ThinkingConfigEnabled withBudget
            && !string.Equals(coerceTo, "enabled", StringComparison.OrdinalIgnoreCase))
        {
            var derived = BudgetToEffort(withBudget.BudgetTokens);
            var oc = (body.OutputConfig ?? new OutputConfig()) with { Effort = derived };
            body = body with { OutputConfig = oc };
        }

        body = body with { Thinking = BuildThinking(coerceTo, body.OutputConfig?.Effort, policy) };
        ctx.Request.Body = body;
        log?.LogDebug(
            "  profile/thinking: '{Profile}' coerced thinking='{From}' → '{To}'",
            profile.CanonicalId, shape, coerceTo);
    }

    private static ThinkingConfig BuildThinking(string shape, string? effort, ThinkingPolicy policy) =>
        shape.ToLowerInvariant() switch
        {
            "enabled"  => new ThinkingConfigEnabled { BudgetTokens = EffortToBudget(effort) },
            "adaptive" => new ThinkingConfigAdaptive(),
            "disabled" => new ThinkingConfigDisabled(),
            _ => throw new InvalidOperationException($"ThinkingPolicy.CoerceToWhenUnsupported='{shape}' is not a valid shape."),
        };

    private static void FoldMidConversationSystem(BridgeContext<MessagesRequest> ctx, ILogger? log)
    {
        var body = ctx.Request.Body;
        if (body.Messages.Count == 0) return;

        var foldedTexts = new List<TextBlockParam>();
        var keptMessages = new List<MessageParam>(body.Messages.Count);

        foreach (var msg in body.Messages)
        {
            if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var block in msg.Content)
                {
                    if (block is TextBlockParam tb)
                    {
                        foldedTexts.Add(tb);
                    }
                }
                continue;
            }
            keptMessages.Add(msg);
        }

        if (foldedTexts.Count == 0) return;

        var newSystem = body.System is { Count: > 0 } existing
            ? existing.Concat(foldedTexts).ToList()
            : foldedTexts;

        ctx.Request.Body = body with
        {
            System = newSystem,
            Messages = keptMessages,
        };
        log?.LogDebug(
            "  profile/mid-conv-system: folded {Count} system message(s) into top-level system field",
            foldedTexts.Count);
    }

    private static void CapThinkingBudget(BridgeContext<MessagesRequest> ctx, ModelProfile profile, ILogger? log)
    {
        if (ctx.Request.Body.Thinking is ThinkingConfigEnabled enabled
            && enabled.BudgetTokens > profile.MaxThinkingBudget)
        {
            ctx.Request.Body = ctx.Request.Body with
            {
                Thinking = enabled with { BudgetTokens = profile.MaxThinkingBudget },
            };
            log?.LogDebug(
                "  profile/budget-cap: clamped thinking.budget_tokens to {Max}",
                profile.MaxThinkingBudget);
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
}

using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Mechanically reshapes the inbound <see cref="MessagesRequest"/> to match
/// what the resolved target <see cref="ModelProfile"/> accepts on the wire.
/// Pure function over (body, profile) → (body, possibly-new-target-model).
/// No user preferences here — those are expressed by the model-redirect rules
/// in <c>appsettings.json</c> that picked the target profile in the first
/// place. Once the target is chosen, every adjustment is determined by the
/// profile's documented behavior, not by user intent.
/// </summary>
/// <remarks>
/// Adjustments, in order:
/// <list type="number">
///   <item><b>Effort + variant routing.</b> If the inbound effort isn't in the
///         profile's <see cref="ModelProfile.AcceptedEfforts"/>, either drop
///         the field (<see cref="EffortHandling.Strip"/>) or rewrite the model
///         id to a sized variant (<see cref="EffortHandling.RouteToVariant"/>).
///         Variant rewrites switch profiles — the adjuster recurses once
///         against the new profile to clean up any further mismatches.</item>
///   <item><b>Thinking shape coercion.</b> If the inbound shape isn't in the
///         profile's accepted set, coerce to
///         <see cref="ThinkingPolicy.CoerceToWhenUnsupported"/>. When the
///         coercion is enabled→adaptive and the policy says so, carry the
///         inbound <c>budget_tokens</c> into <c>output_config.effort</c> so the
///         user's reasoning depth survives. When the coercion lands on
///         enabled, derive a budget from effort.</item>
///   <item><b>Mid-conversation system messages</b> (opus-4.8→4.7 fallback).
///         When the profile rejects them, fold their text into the top-level
///         <c>system</c> field so 4.7 doesn't 400.</item>
///   <item><b>Budget cap.</b> Trim any <c>thinking.budget_tokens</c> exceeding
///         <see cref="ModelProfile.MaxThinkingBudget"/>.</item>
/// </list>
/// Beta passthrough/strip is intentionally NOT done here — that lives in
/// <see cref="Stages.Anthropic.HeadersOutboundStage"/>, which sees the final
/// body shape and derives the outbound beta set from it.
/// </remarks>
internal static class ProfileAdjuster
{
    /// <summary>
    /// Adjust <paramref name="ctx"/>'s body in place to match
    /// <paramref name="profile"/>. Returns the profile of the model the body
    /// ends up addressing — usually the same one passed in, but variant-routing
    /// can switch profiles mid-adjust. Caller uses the returned profile when
    /// resolving the final <c>RouteTarget</c>.
    /// </summary>
    public static ModelProfile Apply(
        BridgeContext<MessagesRequest> ctx, ModelProfile profile, ModelProfileCatalog catalog)
    {
        // Step 1: effort + variant routing. May switch the profile.
        profile = ApplyEffort(ctx, profile, catalog);

        // Step 2: thinking shape coercion (against the possibly-new profile).
        ApplyThinking(ctx, profile);

        // Step 3: mid-conv system fold (opus-4.8 → 4.7 fallback safety).
        if (!profile.AcceptsMidConversationSystem)
        {
            FoldMidConversationSystem(ctx);
        }

        // Step 4: cap thinking.budget_tokens to the profile's ceiling.
        CapThinkingBudget(ctx, profile);

        // Step 5: register beta-strip patterns for HeadersOutboundStage.
        // Globals first — these are betas Copilot's gateway rejects on
        // EVERY model regardless of profile (empirically: claude-code 4.8
        // sends `advisor-tool-2026-03-01` by default and Copilot 400s with
        // "unsupported beta header(s): advisor-tool-2026-03-01"). Listed
        // here rather than on each profile so a new model doesn't silently
        // re-introduce the regression by forgetting to copy the entry.
        ctx.PendingBetaStrips.Add("advisor-tool-*");

        if (profile.StripBetas.Count > 0)
        {
            ctx.PendingBetaStrips.AddRange(profile.StripBetas);
        }

        return profile;
    }

    private static ModelProfile ApplyEffort(
        BridgeContext<MessagesRequest> ctx, ModelProfile profile, ModelProfileCatalog catalog)
    {
        var body = ctx.Request.Body;
        var effort = body.OutputConfig?.Effort;
        if (effort is null) return profile;

        // Inbound effort accepted as-is.
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
                        // Variant ids bake the effort in — strip the field.
                        OutputConfig = body.OutputConfig is { } ocVariant ? ocVariant with { Effort = null } : null,
                    };
                    ctx.Request.Body = body;
                    Log.Debug($"  profile/effort: '{profile.CanonicalId}' + effort='{effort}' → variant '{variantId}' (effort stripped)");
                    return variantProfile;
                }
                // No mapped variant — fall through to strip.
                goto case EffortHandling.Strip;

            case EffortHandling.Strip:
            default:
                body = body with { OutputConfig = body.OutputConfig is { } ocStrip ? ocStrip with { Effort = null } : null };
                ctx.Request.Body = body;
                Log.Debug($"  profile/effort: '{profile.CanonicalId}' rejects effort='{effort}' → stripped");
                return profile;
        }
    }

    private static void ApplyThinking(BridgeContext<MessagesRequest> ctx, ModelProfile profile)
    {
        var body = ctx.Request.Body;
        var shape = ThinkingShape(body.Thinking);
        if (shape is null) return; // No thinking field — nothing to coerce.

        var policy = profile.Thinking;
        foreach (var ok in policy.AcceptedShapes)
        {
            if (string.Equals(ok, shape, StringComparison.OrdinalIgnoreCase))
            {
                // Already an accepted shape; only need to backfill a budget if
                // it's enabled-with-zero-budget on a model that derives it.
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

        // Carry budget → effort BEFORE we lose the budget by replacing
        // Thinking. Only meaningful when coercing enabled (which has a budget)
        // to adaptive (which doesn't).
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
        Log.Debug($"  profile/thinking: '{profile.CanonicalId}' coerced thinking='{shape}' → '{coerceTo}'");
    }

    private static ThinkingConfig BuildThinking(string shape, string? effort, ThinkingPolicy policy) =>
        shape.ToLowerInvariant() switch
        {
            "enabled"  => new ThinkingConfigEnabled { BudgetTokens = EffortToBudget(effort) },
            "adaptive" => new ThinkingConfigAdaptive(),
            "disabled" => new ThinkingConfigDisabled(),
            _ => throw new InvalidOperationException($"ThinkingPolicy.CoerceToWhenUnsupported='{shape}' is not a valid shape."),
        };

    /// <summary>
    /// Fold any non-first <c>role:"system"</c> messages from the
    /// <c>messages</c> array into the top-level <c>system</c> field. Preserves
    /// order. Used when the target profile (e.g. opus-4.7) doesn't accept
    /// opus-4.8's mid-conversation system messages.
    /// </summary>
    private static void FoldMidConversationSystem(BridgeContext<MessagesRequest> ctx)
    {
        var body = ctx.Request.Body;
        if (body.Messages.Count == 0) return;

        var foldedTexts = new List<TextBlockParam>();
        var keptMessages = new List<MessageParam>(body.Messages.Count);

        foreach (var msg in body.Messages)
        {
            if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                // Collect every TextBlockParam in the system message's content
                // for promotion to the top-level system field.
                foreach (var block in msg.Content)
                {
                    if (block is TextBlockParam tb)
                    {
                        foldedTexts.Add(tb);
                    }
                    // Non-text content in a system message is dropped — Anthropic's
                    // top-level system field only accepts text blocks. Mid-conv
                    // system messages are spec'd as text-only anyway.
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
        Log.Debug($"  profile/mid-conv-system: folded {foldedTexts.Count} system message(s) into top-level system field");
    }

    private static void CapThinkingBudget(BridgeContext<MessagesRequest> ctx, ModelProfile profile)
    {
        if (ctx.Request.Body.Thinking is ThinkingConfigEnabled enabled
            && enabled.BudgetTokens > profile.MaxThinkingBudget)
        {
            ctx.Request.Body = ctx.Request.Body with
            {
                Thinking = enabled with { BudgetTokens = profile.MaxThinkingBudget },
            };
            Log.Debug($"  profile/budget-cap: clamped thinking.budget_tokens to {profile.MaxThinkingBudget}");
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

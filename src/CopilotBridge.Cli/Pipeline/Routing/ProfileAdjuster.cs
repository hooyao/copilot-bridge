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

        HandleMidConversationSystem(ctx, profile, log);
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

    /// <summary>
    /// Prefix added to the first text block when a mid-conversation
    /// <c>role:"system"</c> message is rewritten to <c>role:"user"</c>. Gives
    /// the LLM a hint that the message is harness-generated (a Claude Code
    /// task reminder, MCP-server instruction, or queued-while-tool-running
    /// user message) rather than typed by the user. Stable string — changing
    /// it invalidates prompt cache for every existing session, so keep it
    /// frozen unless coordinated with a model-side prompt update.
    /// </summary>
    private const string InjectedSystemMarker = "[Claude Code injected]\n";

    /// <summary>
    /// Handles mid-conversation <c>role:"system"</c> messages in the
    /// inbound body so the outbound shape matches what
    /// <paramref name="profile"/> accepts.
    /// <list type="bullet">
    ///   <item>When <see cref="ModelProfile.AcceptsMidConversationSystem"/>
    ///         is <c>false</c>: every mid-conv <c>role:"system"</c> is
    ///         converted to <c>role:"user"</c> in place, with
    ///         <see cref="InjectedSystemMarker"/> prefixed onto the first
    ///         text block of its content. The top-level <c>system</c> field
    ///         is left untouched — that's what the previous "fold into
    ///         system[]" behavior got wrong (it grew the system field per
    ///         turn, breaking cache for every breakpoint downstream).</item>
    ///   <item>When <c>true</c> (opus-4.8 today): each <c>role:"system"</c>
    ///         is kept in place if its placement is legal under the 4.8
    ///         rule (predecessor is <c>role:"user"</c>; successor is
    ///         <c>role:"assistant"</c> or end-of-array), and converted to
    ///         <c>role:"user"</c> otherwise. Placement is evaluated against
    ///         the original message array — converting one entry never
    ///         changes the predecessor seen by a later entry.</item>
    /// </list>
    /// See <c>docs/bug-mid-conversation-system-messages-dropped.md</c> for
    /// the post-mortem and the cache-impact prototype in
    /// <c>docs/scratch/midconv-cache-prototype.py</c>.
    /// </summary>
    private static void HandleMidConversationSystem(
        BridgeContext<MessagesRequest> ctx, ModelProfile profile, ILogger? log)
    {
        var body = ctx.Request.Body;
        var msgs = body.Messages;
        if (msgs.Count == 0) return;

        // First pass: classify each message as KEEP (non-system, or legally
        // placed system) vs CONVERT (mid-conv system needing rewrite). We
        // walk the ORIGINAL array so conversion of an earlier entry can't
        // change the predecessor seen by a later one.
        var actions = new MessageAction[msgs.Count];
        var convertedCount = 0;
        var keptInPlaceCount = 0;
        var anySystem = false;
        for (var i = 0; i < msgs.Count; i++)
        {
            if (!string.Equals(msgs[i].Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                actions[i] = MessageAction.PassThrough;
                continue;
            }
            anySystem = true;
            if (profile.AcceptsMidConversationSystem && IsLegalSystemPlacement(msgs, i))
            {
                actions[i] = MessageAction.KeepInPlace;
                keptInPlaceCount++;
            }
            else
            {
                actions[i] = MessageAction.ConvertToUser;
                convertedCount++;
            }
        }
        if (!anySystem) return;

        // Second pass: build the new messages array. Only allocate a new
        // list when something actually changed.
        if (convertedCount == 0)
        {
            // All system messages are legally placed; nothing to do.
            log?.LogInformation(
                "  profile/mid-conv-system: profile='{Profile}' accepts={Accepts} kept_in_place={Kept} converted=0",
                profile.CanonicalId, profile.AcceptsMidConversationSystem, keptInPlaceCount);
            return;
        }

        var rebuilt = new List<MessageParam>(msgs.Count);
        for (var i = 0; i < msgs.Count; i++)
        {
            rebuilt.Add(actions[i] == MessageAction.ConvertToUser
                ? ConvertSystemToUser(msgs[i])
                : msgs[i]);
        }
        ctx.Request.Body = body with { Messages = rebuilt };

        log?.LogInformation(
            "  profile/mid-conv-system: profile='{Profile}' accepts={Accepts} kept_in_place={Kept} converted={Converted}",
            profile.CanonicalId, profile.AcceptsMidConversationSystem, keptInPlaceCount, convertedCount);
    }

    /// <summary>
    /// True iff <c>messages[i]</c> (a <c>role:"system"</c> entry) satisfies
    /// opus-4.8's placement rule: predecessor is <c>role:"user"</c> AND
    /// successor is <c>role:"assistant"</c> or end-of-array. Empirically
    /// verified against Copilot 2026-06-05; see
    /// <c>tests/CopilotBridge.Playground/ModelProfileProbe.Opus48_MidConversationSystem_PlacementRules</c>.
    /// The "assistant ending in a server tool result" branch of the
    /// documented rule is unreachable from Claude Code (it never emits
    /// server-tool blocks), so we conservatively require <c>user</c> as the
    /// predecessor.
    /// </summary>
    private static bool IsLegalSystemPlacement(IReadOnlyList<MessageParam> msgs, int i)
    {
        var prev = i > 0 ? msgs[i - 1] : null;
        var next = i + 1 < msgs.Count ? msgs[i + 1] : null;
        var predOk = prev is not null
            && string.Equals(prev.Role, "user", StringComparison.OrdinalIgnoreCase);
        var succOk = next is null
            || string.Equals(next.Role, "assistant", StringComparison.OrdinalIgnoreCase);
        return predOk && succOk;
    }

    /// <summary>
    /// Returns a copy of <paramref name="msg"/> with <c>Role = "user"</c> and
    /// the <see cref="InjectedSystemMarker"/> prepended to the first text
    /// block's text. <see cref="CacheControl"/> on the original text block
    /// is preserved (Claude Code does not set it on mid-conv system entries
    /// in practice, but we keep the invariant clean). Non-text content
    /// blocks (none seen in real traces) are passed through unchanged.
    /// </summary>
    private static MessageParam ConvertSystemToUser(MessageParam msg)
    {
        var content = msg.Content;
        var rebuiltContent = new List<ContentBlockParam>(content.Count > 0 ? content.Count : 1);
        var markerInjected = false;
        for (var j = 0; j < content.Count; j++)
        {
            var block = content[j];
            if (!markerInjected && block is TextBlockParam tb)
            {
                rebuiltContent.Add(tb with { Text = InjectedSystemMarker + tb.Text });
                markerInjected = true;
            }
            else
            {
                rebuiltContent.Add(block);
            }
        }
        if (!markerInjected)
        {
            // No text block to prefix (empty content, or only non-text blocks
            // — both pathological for a system message but tolerated). Inject
            // the marker as a leading text block so the LLM still sees the
            // signal.
            rebuiltContent.Insert(0, new TextBlockParam { Text = InjectedSystemMarker });
        }
        return msg with { Role = "user", Content = rebuiltContent };
    }

    private enum MessageAction
    {
        PassThrough,
        KeepInPlace,
        ConvertToUser,
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

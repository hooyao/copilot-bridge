namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// The wire-level truth for one Copilot Anthropic model: what the
/// <c>POST /v1/messages</c> backend behind that model id <i>actually</i>
/// accepts. A profile is an immutable fact about Copilot's API surface, learned
/// by probing the endpoint with the Playground client —
/// NOT derived from Copilot's <c>/models</c> response, whose advertised
/// capabilities are incomplete and sometimes wrong (haiku-4.5 advertises
/// adaptive thinking but rejects it at runtime).
/// </summary>
/// <remarks>
/// <para>Users cannot override a profile — it is Copilot's behavior, not a
/// preference. What users <i>can</i> configure (in <c>appsettings.json</c>
/// <c>Routing.Locations</c>) is a model redirect. Once it picks a target, the
/// target's profile decides every body adjustment mechanically via
/// <see cref="ProfileAdjuster"/>.</para>
/// <para>No unprobed model receives a catalog entry. The fuzzy nearest-profile
/// fallback is a best-effort bridge until that id is probed.</para>
/// </remarks>
internal sealed record ModelProfile
{
    /// <summary>Canonical model id this profile describes (e.g. <c>claude-opus-5.5</c>).</summary>
    public required string CanonicalId { get; init; }

    /// <summary>
    /// <c>output_config.effort</c> values the backend accepts as-is. Empty =
    /// the model rejects the effort field outright (it must be stripped). For
    /// variant-locked models (e.g. <c>-high</c>, <c>-xhigh</c>) the effort is
    /// baked into the id, so the field is always stripped and this is empty.
    /// </summary>
    public IReadOnlyList<string> AcceptedEfforts { get; init; } = [];

    /// <summary>
    /// What to do when the inbound effort is not in <see cref="AcceptedEfforts"/>.
    /// </summary>
    public EffortHandling EffortOnUnsupported { get; init; } = EffortHandling.Strip;

    /// <summary>
    /// Map from an inbound effort value to a sibling model id that locks that
    /// effort. e.g. <c>{ "high": "claude-opus-4.7-high" }</c>. Consulted only
    /// when <see cref="EffortOnUnsupported"/> is
    /// <see cref="EffortHandling.RouteToVariant"/>. The chosen variant has its
    /// own profile; the adjuster re-resolves against it.
    /// <para><b>Dormant as of the 2026-07 reconciliation:</b> no profile uses
    /// <see cref="EffortHandling.RouteToVariant"/> today. Copilot retired every
    /// sized sibling id (<c>-high</c> / <c>-xhigh</c>) and widened the base
    /// models to accept the full effort range directly, so there is nothing to
    /// route to. The mechanism is kept because the pattern can return, but the
    /// example id above no longer resolves — treat it as illustrative only.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> EffortToVariant { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the backend handles each <c>thinking</c> shape.</summary>
    public ThinkingPolicy Thinking { get; init; } = ThinkingPolicy.AdaptiveOnly;

    /// <summary>
    /// Effort used when this model rejects an explicit <c>thinking:disabled</c>
    /// and the profile must coerce it to adaptive. A lower accepted tier keeps
    /// the request closer to the caller's intent to avoid reasoning cost.
    /// Null preserves a caller-supplied effort during the coercion.
    /// </summary>
    public string? EffortWhenDisabledThinkingUnsupported { get; init; }

    /// <summary>
    /// Upper bound for <c>thinking.budget_tokens</c> the backend tolerates.
    /// Used when deriving a budget from effort so we never exceed it.
    /// </summary>
    public int MaxThinkingBudget { get; init; } = 64000;

    /// <summary>
    /// True if the backend accepts <c>role:"system"</c> messages in non-first
    /// positions of the <c>messages</c> array in legal placements. The current
    /// Opus 5.5 profile accepts them under the user-predecessor rule.
    /// <para>
    /// When <c>true</c>, <see cref="ProfileAdjuster"/> keeps each mid-conv
    /// <c>role:"system"</c> in place if its placement is legal under the 4.8
    /// rule (predecessor is <c>user</c>; successor is <c>assistant</c> or
    /// end-of-array) and converts to <c>role:"user"</c> with an
    /// injected-context prefix otherwise. When <c>false</c>, every mid-conv
    /// <c>role:"system"</c> is converted unconditionally — see the bug
    /// post-mortem <c>docs/bug-mid-conversation-system-messages-dropped.md</c>
    /// for why folding into the top-level <c>system</c> field (the previous
    /// behavior) lost user input and broke cache.
    /// </para>
    /// </summary>
    public bool AcceptsMidConversationSystem { get; init; }

    /// <summary>
    /// Whether <c>tool_choice:{"type":"any"}</c> and
    /// <c>tool_choice:{"type":"tool"}</c> are accepted. When false, the
    /// adjuster preserves the available tools but uses <c>auto</c> so the
    /// request can reach a model that does not support forced tool selection.
    /// </summary>
    public bool SupportsForcedToolChoice { get; init; } = true;

    /// <summary>
    /// Whether the backend accepts top-level <c>speed:"fast"</c>. The bridge's
    /// DTO does not model speed, so the flag remains false until both a live
    /// Copilot probe and a client path justify supporting it.
    /// </summary>
    public bool AcceptsSpeedFast { get; init; }

    /// <summary>
    /// <c>anthropic-beta</c> tokens to strip from the outbound header set when
    /// this profile is the active target. Patterns may end with <c>*</c>
    /// (trailing wildcard). Used for tokens that are semantically subsumed by
    /// the model id itself — e.g. the dedicated <c>-1m</c> / <c>-1m-internal</c>
    /// variants already imply 1M context, so forwarding the
    /// <c>context-1m-*</c> beta is at best redundant and at worst rejected.
    /// </summary>
    public IReadOnlyList<string> StripBetas { get; init; } = [];

    /// <summary>
    /// Efforts the backend rejects <b>only when <c>thinking</c> is
    /// <c>disabled</c></b> — a CROSS-FIELD constraint: each field is
    /// individually valid, but the pair 400s. Empty (the default) means the
    /// model has no such interaction and effort validity depends solely on
    /// <see cref="AcceptedEfforts"/>.
    /// <para>Introduced for <c>claude-opus-5</c>, the first Copilot model to
    /// enforce one (probed 2026-07:
    /// <c>ModelProfileProbe.Opus5_DisabledThinking_EffortInteraction_Probe</c> —
    /// <c>thinking:disabled</c> + <c>xhigh</c>/<c>max</c> → 400 <i>"effort 'max'
    /// is not supported when thinking is disabled on this model. Use effort
    /// 'high' or below, or enable thinking"</i>, while the same efforts are 200
    /// with thinking on and <c>disabled</c> is 200 at <c>high</c> and below).</para>
    /// <para><b>Why this needs its own field rather than a narrower
    /// <see cref="AcceptedEfforts"/>:</b> narrowing the list would strip
    /// <c>xhigh</c>/<c>max</c> unconditionally and silently downgrade every
    /// thinking-ON request — the common case — to reach a constraint that only
    /// binds when thinking is off. <see cref="ProfileAdjuster"/> therefore
    /// consults this only on the disabled-thinking path, where it clamps to the
    /// highest still-accepted effort (preserving the user's intent as far as the
    /// backend allows) instead of dropping the field entirely.</para>
    /// </summary>
    public IReadOnlyList<string> EffortsRejectedWhenThinkingDisabled { get; init; } = [];
}

/// <summary>What to do with an <c>output_config.effort</c> the model won't take.</summary>
internal enum EffortHandling
{
    /// <summary>Drop the field; let the model use its default effort.</summary>
    Strip,

    /// <summary>Re-route to a sibling model id from <see cref="ModelProfile.EffortToVariant"/>.</summary>
    RouteToVariant,
}

/// <summary>
/// How a model handles the three <c>thinking</c> shapes
/// (<c>enabled</c> / <c>adaptive</c> / <c>disabled</c>) and how to coerce an
/// unsupported one into a supported one.
/// </summary>
internal sealed record ThinkingPolicy
{
    /// <summary>Shapes accepted as-is: subset of <c>enabled, adaptive, disabled</c>.</summary>
    public required IReadOnlyList<string> AcceptedShapes { get; init; }

    /// <summary>
    /// Shape to coerce to when the inbound shape is not in
    /// <see cref="AcceptedShapes"/>. Must itself be an accepted shape.
    /// </summary>
    public required string CoerceToWhenUnsupported { get; init; }

    /// <summary>
    /// When the (possibly-coerced) shape is <c>enabled</c>, set
    /// <c>thinking.budget_tokens</c> from <c>output_config.effort</c>. Used for
    /// models that take explicit-budget thinking but not adaptive (haiku-4.5).
    /// </summary>
    public bool DeriveBudgetFromEffortOnEnabled { get; init; }

    /// <summary>
    /// When the inbound shape is <c>enabled</c> but the model wants adaptive,
    /// carry the inbound <c>budget_tokens</c> forward into
    /// <c>output_config.effort</c> before coercing the shape — so the user's
    /// requested reasoning depth survives the enabled→adaptive rewrite.
    /// </summary>
    public bool DeriveEffortFromBudgetOnCoerce { get; init; }

    /// <summary>opus-4.7 / opus-4.8 base: adaptive only; coerce enabled/disabled → adaptive.</summary>
    public static ThinkingPolicy AdaptiveOnly { get; } = new()
    {
        AcceptedShapes = ["adaptive"],
        CoerceToWhenUnsupported = "adaptive",
        DeriveEffortFromBudgetOnCoerce = true,
    };

    /// <summary>
    /// opus-5: <c>adaptive</c> AND <c>disabled</c> both accepted; only
    /// <c>enabled</c> is rejected (400 "Use thinking.type.adaptive and
    /// output_config.effort") and is coerced to adaptive.
    /// <para><b>Why this is not <see cref="AdaptiveOnly"/>:</b> that policy lists
    /// only <c>adaptive</c> as accepted, so it coerces an inbound
    /// <c>thinking:disabled</c> up to adaptive — silently re-enabling reasoning
    /// the user explicitly turned off, and billing them for the thinking tokens.
    /// <c>Opus5_Thinking_ProbeAcceptance</c> shows Copilot returns 200 for
    /// <c>disabled</c> on opus-5, so there is no backend reason to rewrite it.
    /// Keeping <c>disabled</c> is also what makes
    /// <see cref="ModelProfile.EffortsRejectedWhenThinkingDisabled"/> reachable —
    /// under <see cref="AdaptiveOnly"/> the disabled shape never survives to the
    /// wire, so the cross-field clamp would be dead code.</para>
    /// <para>NOTE: opus-4.7 / opus-4.8 / sonnet-5 probe the same way
    /// (<c>disabled</c> → 200) but still carry <see cref="AdaptiveOnly"/>. That
    /// pre-existing coercion is untouched here — changing it alters live behavior
    /// for three shipped models and needs its own real-client verification.</para>
    /// </summary>
    public static ThinkingPolicy AdaptiveOrDisabled { get; } = new()
    {
        AcceptedShapes = ["adaptive", "disabled"],
        CoerceToWhenUnsupported = "adaptive",
        DeriveEffortFromBudgetOnCoerce = true,
    };

    /// <summary>haiku-4.5: advertises adaptive, rejects it; only explicit-enabled works.</summary>
    public static ThinkingPolicy EnabledOnly { get; } = new()
    {
        AcceptedShapes = ["enabled", "disabled"],
        CoerceToWhenUnsupported = "enabled",
        DeriveBudgetFromEffortOnEnabled = true,
    };

    /// <summary>Models that take any of the three shapes without rewrite.</summary>
    public static ThinkingPolicy All { get; } = new()
    {
        AcceptedShapes = ["enabled", "adaptive", "disabled"],
        CoerceToWhenUnsupported = "adaptive",
    };
}

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// The wire-level truth for one Copilot Anthropic model: what the
/// <c>POST /v1/messages</c> backend behind that model id <i>actually</i>
/// accepts. A profile is an immutable fact about Copilot's API surface, learned
/// by probing the endpoint with the Anthropic NodeJS SDK in the playground —
/// NOT derived from Copilot's <c>/models</c> response, whose advertised
/// capabilities are incomplete and sometimes wrong (haiku-4.5 advertises
/// adaptive thinking but rejects it at runtime).
/// </summary>
/// <remarks>
/// <para>Users cannot override a profile — it is Copilot's behavior, not a
/// preference. What users <i>can</i> configure (in <c>appsettings.json</c>
/// <c>Routing.Rules</c>) is a model-redirect: "dress an inbound opus-4.9
/// request in opus-4.8's clothes". Once the redirect picks a target model, the
/// target's profile decides every body adjustment mechanically via
/// <see cref="ProfileAdjuster"/>.</para>
/// <para>When a model's behavior has not yet been confirmed in the playground,
/// the catalog entry is marked with a <c>// PLAYGROUND-PENDING</c> comment.
/// Those values are best-effort extrapolations from the nearest confirmed
/// family member and must be verified before they are trusted.</para>
/// </remarks>
internal sealed record ModelProfile
{
    /// <summary>Canonical model id this profile describes (e.g. <c>claude-opus-4.7-1m-internal</c>).</summary>
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
    /// </summary>
    public IReadOnlyDictionary<string, string> EffortToVariant { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the backend handles each <c>thinking</c> shape.</summary>
    public ThinkingPolicy Thinking { get; init; } = ThinkingPolicy.AdaptiveOnly;

    /// <summary>
    /// Upper bound for <c>thinking.budget_tokens</c> the backend tolerates.
    /// Used when deriving a budget from effort so we never exceed it.
    /// </summary>
    public int MaxThinkingBudget { get; init; } = 64000;

    /// <summary>
    /// True only for opus-4.8+: the backend accepts <c>role:"system"</c>
    /// messages in non-first positions of the <c>messages</c> array. When
    /// false and such messages are present, <see cref="ProfileAdjuster"/> folds
    /// them into the top-level <c>system</c> field so a 4.8→4.7 fallback does
    /// not 400.
    /// </summary>
    public bool AcceptsMidConversationSystem { get; init; }

    /// <summary>
    /// True only for opus-4.8+: the backend accepts the top-level
    /// <c>speed:"fast"</c> field. The bridge's DTO does not model
    /// <c>speed</c> today (it is dropped at deserialize time), so this is a
    /// forward-looking flag — kept so the fact is recorded even before the DTO
    /// grows the field.
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

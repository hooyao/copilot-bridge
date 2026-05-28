namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Routing</c> via the standard
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> /
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> pipeline.
/// Loaded once at startup; <c>reloadOnChange</c> is off — edit the file and
/// restart the bridge. Validated by <see cref="RoutesValidator"/> immediately
/// after binding — invalid config fails the process before Kestrel binds the
/// port (fail-fast, no silent fallback).
/// </summary>
/// <remarks>
/// Two layers (hardcoded order, no iteration / no fixed-point):
/// <list type="number">
///   <item><b>User rules</b> (this file, JSON) — the operator's preferences:
///         "route opus-4.7 to opus-4.6", "rewrite effort=max to xhigh", and
///         protocol quirks the operator can patch when Copilot ships a new
///         model before we cut a release. Each rule is a Match → Rewrite
///         pair; the rule list is scanned once, first-match-wins.</item>
///   <item><b>Capability</b> (in <see cref="CopilotModelRegistry"/>, C#) —
///         the model's physical truth: which models accept
///         <c>output_config.effort</c>, and which efforts trigger a sized
///         variant (e.g. opus-4.7 + high → opus-4.7-high). Capability runs
///         <i>after</i> user rules and gives the outbound model's
///         constraints the final word, except for fields the user
///         explicitly set in their Rewrite — those bypass capability so
///         "I really want effort=xhigh on this model" round-trips.</item>
/// </list>
/// </remarks>
internal sealed class RoutesConfig
{
    /// <summary>Single linear scan, first-match-wins.</summary>
    public List<RoutingRule> Rules { get; set; } = [];
}

internal sealed class RoutingRule
{
    public RouteMatch Match { get; set; } = new();
    public RuleRewrite? Rewrite { get; set; }
    /// <summary>Free-form developer comment; runtime-ignored, kept in diag log.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// Match conditions. Every non-null field must match the request's
/// corresponding value exactly (case-insensitive). null = no constraint on
/// that dimension.
/// </summary>
internal sealed class RouteMatch
{
    /// <summary>Canonical model id (post-<c>CopilotModelRegistry.Normalize</c>).</summary>
    public string? InboundModel { get; set; }
    /// <summary>Effort level requested in <c>output_config.effort</c>.</summary>
    public string? InboundEffort { get; set; }
    /// <summary>Thinking shape: <c>"enabled"</c> | <c>"adaptive"</c> | <c>"disabled"</c>.</summary>
    public string? InboundThinking { get; set; }
}

/// <summary>
/// Outbound rewrites. Any non-null field replaces the corresponding inbound
/// field; null/absent means "leave alone (capability layer may still adjust
/// it)". Setting a field marks it as user-explicit, which makes the
/// capability layer trust the user's decision instead of stripping the
/// field on physical-fact grounds.
/// </summary>
internal sealed class RuleRewrite
{
    /// <summary>Replace the outbound model (canonical id, e.g. <c>claude-opus-4.6</c>).</summary>
    public string? Model { get; set; }

    /// <summary>Replace the outbound <c>output_config.effort</c> value.</summary>
    public string? Effort { get; set; }

    /// <summary>Replace the outbound thinking shape.</summary>
    public ThinkingRewrite? Thinking { get; set; }

    /// <summary>
    /// After applying <see cref="Thinking"/> (or if thinking is already
    /// <c>enabled</c>), set <c>thinking.budget_tokens</c> from
    /// <c>output_config.effort</c> using the standard mapping
    /// (low=4096, medium=16384, high=32768, xhigh/max=64000).
    /// </summary>
    public bool DeriveBudgetFromEffort { get; set; }

    /// <summary>
    /// When the (post-Rewrite) thinking is <c>enabled</c>, derive
    /// <c>output_config.effort</c> from <c>thinking.budget_tokens</c>
    /// using the inverse of the standard mapping. Marks effort as
    /// user-explicit.
    /// </summary>
    public bool DeriveEffortFromBudget { get; set; }
}

/// <summary>
/// Rewrite the thinking shape. <see cref="Type"/> picks the kind;
/// <see cref="BudgetTokens"/> only applies to <c>"enabled"</c>.
/// </summary>
internal sealed class ThinkingRewrite
{
    /// <summary><c>"enabled"</c> | <c>"adaptive"</c> | <c>"disabled"</c>.</summary>
    public string? Type { get; set; }
    public int? BudgetTokens { get; set; }
}

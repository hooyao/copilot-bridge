namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Wire-level truth for one Copilot <c>/responses</c> (Codex) model: which
/// <c>reasoning.effort</c> values the backend accepts, and the per-model quirks
/// the request translation (T2) must respect. Like <see cref="ModelProfile"/> on
/// the Anthropic side, this is an immutable FACT about Copilot's behavior, sourced
/// row-by-row from the live contract snapshot
/// (<c>docs/copilot-responses-contract-snapshot.json</c>, change 2) — NEVER
/// extrapolated from family names. When the snapshot drifts (change-2's B2 goes
/// red), reconcile these rows.
/// </summary>
internal sealed record CodexModelProfile
{
    /// <summary>Canonical model id (e.g. <c>gpt-5.3-codex</c>).</summary>
    public required string CanonicalId { get; init; }

    /// <summary>
    /// The <c>reasoning.effort</c> values this model accepts as-is. T2 clamps an
    /// inbound effort not in this set to the nearest accepted value (or drops it).
    /// Two inverted profiles exist (research §2.2): "large" accept
    /// <c>none/low/medium/high/xhigh</c> (reject <c>minimal</c>); "small" accept
    /// <c>minimal/low/medium/high</c> (reject <c>none</c>+<c>xhigh</c>).
    /// </summary>
    public required IReadOnlyList<string> AcceptedEfforts { get; init; }

    /// <summary>
    /// True for <c>mai-code-1-flash-internal</c>: Copilot 500s on custom/grammar
    /// tools (e.g. <c>apply_patch</c>) for this model (research §2.4 / snapshot
    /// <c>tools_rejected</c>). Recorded as a fact, not silently surfaced as a
    /// bridge bug; T2 may drop custom tools for this model rather than 500.
    /// </summary>
    public bool RejectsCustomTools { get; init; }
}

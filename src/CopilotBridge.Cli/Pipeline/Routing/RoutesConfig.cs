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
/// <para>Routing is organized nginx-style: a request matches at most one
/// <see cref="RouteLocation"/> (first-match-wins), and that location's
/// <see cref="LocationUse"/> declares the complete change-set applied to the
/// request — backend model, effort remapping for this target, header tweaks.
/// There is no chain, no fall-through, no <c>StopWhenMatched</c>: each
/// location is a self-contained closure.</para>
/// <para>After the location's <c>Use</c> is applied, the target's
/// <see cref="ModelProfile"/> still runs over the rewritten body —
/// profile-derived guarantees (thinking shape coercion, beta strips,
/// mid-conv-system fold) are layered <i>after</i> user routing.</para>
/// </remarks>
internal sealed class RoutesConfig
{
    /// <summary>Top-to-bottom, first-match-wins.</summary>
    public List<RouteLocation> Locations { get; set; } = [];
}

/// <summary>
/// A self-contained routing entry: the <see cref="When"/> match plus the
/// <see cref="Use"/> change-set fired on a match. Modeled after nginx
/// <c>location { ... }</c> — everything that should happen for "this kind
/// of request" lives in one block.
/// </summary>
internal sealed class RouteLocation
{
    public MatchExpression When { get; set; } = new();
    public LocationUse Use { get; set; } = new();
    /// <summary>Free-form developer comment; runtime-ignored, kept in diag log.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// What this location does when matched. All fields are optional — an empty
/// <c>Use</c> is a no-op and rejected by <see cref="RoutesValidator"/>.
/// </summary>
internal sealed class LocationUse
{
    /// <summary>Replace the outbound model id (canonical form, e.g. <c>claude-opus-4.7-1m-internal</c>).</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Per-target effort remapping. Keys are inbound effort values
    /// (case-insensitive); the matching key's value replaces
    /// <c>output_config.effort</c> before <see cref="ProfileAdjuster"/> runs.
    /// Lives on the location rather than as a separate rule because the
    /// mapping is specific to <see cref="Model"/> — e.g.
    /// <c>{"max":"xhigh"}</c> makes sense on <c>claude-opus-4.7-1m-internal</c>
    /// (the only Copilot model that accepts xhigh natively) but would be
    /// wrong on <c>claude-opus-4.8</c> (no xhigh variant exists).
    /// </summary>
    public Dictionary<string, string>? EffortMap { get; set; }

    /// <summary>Header overrides. Whitelisted at startup by <see cref="RoutesValidator"/>.</summary>
    public LocationHeaders? Headers { get; set; }
}

/// <summary>
/// Header rewrites. Only a small whitelist of header names is accepted
/// (operator-tunable Copilot identity headers + <c>anthropic-beta</c>);
/// names outside the whitelist fail startup validation. This keeps users
/// from clobbering bridge-internal protocol headers (<c>Authorization</c>,
/// session/device ids) and producing silent 401s.
/// </summary>
internal sealed class LocationHeaders
{
    /// <summary>
    /// Set or replace headers, name → value. For multi-token headers like
    /// <c>anthropic-beta</c> the value is taken verbatim (comma-joined token
    /// list); use <see cref="Remove"/> if you only want to drop specific
    /// tokens rather than replace the whole header.
    /// </summary>
    public Dictionary<string, string>? Set { get; set; }

    /// <summary>
    /// Remove headers (or specific tokens). Plain entries (<c>"X-Foo"</c>)
    /// drop the whole header. For comma-token headers the form
    /// <c>"anthropic-beta:context-1m-*"</c> drops only the matching token(s);
    /// trailing <c>*</c> is a wildcard. Patterns without <c>:</c> match
    /// whole-header by name (case-insensitive).
    /// </summary>
    public List<string>? Remove { get; set; }
}

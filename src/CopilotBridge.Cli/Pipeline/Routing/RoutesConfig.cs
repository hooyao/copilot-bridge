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
    /// <summary>Replace the outbound model id (canonical form, e.g. <c>gpt-5.6-sol</c>).</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Per-target effort remapping. Keys are inbound effort values
    /// (case-insensitive); the matching key's value replaces
    /// <c>output_config.effort</c> before <see cref="ProfileAdjuster"/> runs.
    /// Lives on the location rather than as a separate rule because the mapping
    /// is specific to <see cref="Model"/>. Two distinct uses:
    /// <list type="bullet">
    ///   <item><b>Down-tier a value the target accepts.</b> <c>{"max":"xhigh"}</c>
    ///   on <c>gpt-5.6-sol</c>, which takes <c>max</c> natively — without the map
    ///   <c>max</c> would pass through verbatim; the map caps it deliberately.</item>
    ///   <item><b>Preserve intent where the profile would otherwise strip.</b> If
    ///   the target rejects <c>max</c> but accepts <c>xhigh</c>, mapping
    ///   <c>max→xhigh</c> keeps the highest supported tier instead of letting
    ///   <see cref="ProfileAdjuster"/> drop the field and fall back to the model
    ///   default — which may be lower than the user asked for.</item>
    /// </list>
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

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Sanity checks the routes config at startup. Fail-fast: any defect throws
/// before Kestrel binds the port. Better a clear startup error than a silent
/// misbehavior at request time.
/// </summary>
internal static class RoutesValidator
{
    /// <summary>
    /// HTTP headers a routing location may <c>Set</c> or <c>Remove</c>.
    /// Bridge-internal protocol headers (<c>Authorization</c>, session/device
    /// IDs, <c>Copilot-Vision-Request</c>) are deliberately omitted — letting
    /// users override them produces silent auth failures or wrong-routing.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>anthropic-beta</c> — token-level Add/Remove on the beta set
    ///         (see <c>HeadersOutboundStage</c>). Use case: per-target opt-in
    ///         or opt-out of a beta the profile doesn't cover.</item>
    ///   <item><c>Editor-Version</c> / <c>Editor-Plugin-Version</c> — override
    ///         the static VS Code identity Copilot uses for feature gating.
    ///         Use case: chase a Copilot feature rollout that only lights up
    ///         for newer editor versions.</item>
    ///   <item><c>Copilot-Integration-Id</c> — override the billing/quota
    ///         bucket name. Use case: route specific locations to a different
    ///         integration id for cost accounting.</item>
    ///   <item><c>X-GitHub-Api-Version</c> — pin a specific CAPI date for
    ///         protocol-change testing.</item>
    /// </list>
    /// </remarks>
    private static readonly HashSet<string> AllowedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "anthropic-beta",
        "Editor-Version",
        "Editor-Plugin-Version",
        "Copilot-Integration-Id",
        "X-GitHub-Api-Version",
    };

    public static void Validate(RoutesConfig config)
    {
        for (var i = 0; i < config.Locations.Count; i++)
        {
            var loc = config.Locations[i];
            var prefix = $"appsettings.json: Routing.Locations[{i}]";

            ValidateMatch(loc.When, $"{prefix}.When");
            ValidateUse(loc.Use, $"{prefix}.Use");
        }
    }

    private static void ValidateMatch(MatchExpression m, string prefix)
    {
        // Composites must be non-empty arrays when present; an empty AllOf/AnyOf
        // is almost always an authoring slip and the semantics ("vacuously true"
        // vs "vacuously false") differ between them in a non-obvious way.
        if (m.AllOf is { Count: 0 })
            throw new InvalidOperationException($"{prefix}.AllOf is an empty array — remove it or add child conditions.");
        if (m.AnyOf is { Count: 0 })
            throw new InvalidOperationException($"{prefix}.AnyOf is an empty array — remove it or add child conditions.");

        if (m.AllOf is { } all)
            for (var i = 0; i < all.Count; i++) ValidateMatch(all[i], $"{prefix}.AllOf[{i}]");
        if (m.AnyOf is { } any)
            for (var i = 0; i < any.Count; i++) ValidateMatch(any[i], $"{prefix}.AnyOf[{i}]");

        if (m.Header is { } h)
        {
            if (string.IsNullOrWhiteSpace(h.Name))
                throw new InvalidOperationException($"{prefix}.Header.Name is required (e.g. 'anthropic-beta').");
            if (h.Eq is null && h.Contains is null)
                throw new InvalidOperationException($"{prefix}.Header must set either 'Eq' or 'Contains'.");
            if (h.Eq is not null && h.Contains is not null)
                throw new InvalidOperationException($"{prefix}.Header sets both 'Eq' and 'Contains'; pick one.");
        }
    }

    private static void ValidateUse(LocationUse u, string prefix)
    {
        var hasModel = !string.IsNullOrWhiteSpace(u.Model);
        var hasEffortMap = u.EffortMap is { Count: > 0 };
        var hasSet = u.Headers?.Set is { Count: > 0 };
        var hasRemove = u.Headers?.Remove is { Count: > 0 };

        if (!hasModel && !hasEffortMap && !hasSet && !hasRemove)
        {
            throw new InvalidOperationException(
                $"{prefix} is empty — set at least one of Model, EffortMap, Headers.Set, Headers.Remove. "
                + "An empty Use block has no effect (the location would silently no-op).");
        }

        if (u.Headers?.Set is { } setMap)
        {
            foreach (var (name, value) in setMap)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException($"{prefix}.Headers.Set contains an empty header name.");
                if (!AllowedHeaderNames.Contains(name))
                {
                    throw new InvalidOperationException(
                        $"{prefix}.Headers.Set['{name}'] is not in the allow-list. "
                        + $"Allowed: {string.Join(", ", AllowedHeaderNames)}. "
                        + "Other headers are managed by the bridge (Authorization, session/machine IDs, vision flag) and cannot be overridden.");
                }
                if (value is null)
                    throw new InvalidOperationException(
                        $"{prefix}.Headers.Set['{name}'] is null — use Headers.Remove to drop a header, "
                        + "or set a non-null value to override.");
            }
        }

        if (u.Headers?.Remove is { } removeList)
        {
            for (var j = 0; j < removeList.Count; j++)
            {
                var entry = removeList[j];
                if (string.IsNullOrWhiteSpace(entry))
                    throw new InvalidOperationException($"{prefix}.Headers.Remove[{j}] is empty.");

                // Token form: "header-name:pattern". Only meaningful for
                // anthropic-beta today (single-value headers have no per-token
                // semantics).
                var colon = entry.IndexOf(':');
                var hName = colon > 0 ? entry[..colon] : entry;
                if (!AllowedHeaderNames.Contains(hName))
                {
                    throw new InvalidOperationException(
                        $"{prefix}.Headers.Remove[{j}]='{entry}' targets header '{hName}', which is not in the allow-list. "
                        + $"Allowed: {string.Join(", ", AllowedHeaderNames)}.");
                }
                if (colon > 0 && !string.Equals(hName, "anthropic-beta", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{prefix}.Headers.Remove[{j}]='{entry}' uses token form ('name:pattern') but the header isn't anthropic-beta. "
                        + "Other headers are single-value — use the plain form (without ':') to remove the whole header.");
                }
            }
        }

        if (u.EffortMap is { } map)
        {
            foreach (var (k, v) in map)
            {
                if (string.IsNullOrWhiteSpace(k))
                    throw new InvalidOperationException($"{prefix}.EffortMap contains an empty key.");
                if (string.IsNullOrWhiteSpace(v))
                    throw new InvalidOperationException($"{prefix}.EffortMap['{k}'] has an empty value — to drop the field instead, omit the location and let the profile's effort handling decide.");
            }
        }
    }
}

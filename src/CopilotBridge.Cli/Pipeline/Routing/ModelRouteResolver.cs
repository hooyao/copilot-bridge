using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// nginx-style location resolver: scan <see cref="RoutesConfig.Locations"/>
/// top-to-bottom, fire the first one whose <see cref="RouteLocation.When"/>
/// matches, apply its <see cref="LocationUse"/>, return. No chain, no
/// fall-through, no <c>StopWhenMatched</c> — each location is a complete
/// closure (when this case matches, do all of this).
/// </summary>
/// <remarks>
/// Profile-driven body coercion (thinking shape, beta strips, mid-conv-system
/// fold, budget cap) still runs <i>after</i> this resolver via
/// <see cref="ProfileAdjuster"/>; user routing only sets the inbound shape
/// (model, effort, headers) and the profile takes it from there.
/// </remarks>
internal static class ModelRouteResolver
{
    /// <summary>
    /// Walk locations; on the first match, apply the rewrite and return
    /// <c>(location, index)</c>. Returns <c>(null, -1)</c> when no location
    /// matched. Logger is injected by the caller (<see cref="Stages.Anthropic.ModelRouterStage"/>)
    /// so this static helper doesn't need its own DI registration.
    /// </summary>
    public static (RouteLocation? Matched, int Index) Apply(
        BridgeContext<MessagesRequest> ctx, RoutesConfig config, ILogger<ModelRouteResolverLog>? log = null)
    {
        for (var i = 0; i < config.Locations.Count; i++)
        {
            var loc = config.Locations[i];
            if (!loc.When.Matches(ctx)) continue;

            ApplyUse(ctx, loc.Use, log);
            log?.LogDebug("  routes/location[{Index}]: matched [{Note}]", i, loc.Note ?? "—");
            return (loc, i);
        }
        return (null, -1);
    }

    private static void ApplyUse(BridgeContext<MessagesRequest> ctx, LocationUse use, ILogger? log)
    {
        var body = ctx.Request.Body;
        var bodyChanged = false;

        if (use.Model is { Length: > 0 } newModel
            && !string.Equals(newModel, body.Model, StringComparison.OrdinalIgnoreCase))
        {
            body = body with { Model = newModel };
            bodyChanged = true;
        }

        // EffortMap: lookup by current effort. The lookup runs AFTER the model
        // swap above, so a location can say "on this target, max → xhigh"
        // independently of what the inbound model was.
        if (use.EffortMap is { Count: > 0 } map
            && body.OutputConfig?.Effort is { Length: > 0 } inboundEffort
            && map.TryGetValue(inboundEffort, out var mappedEffort)
            && !string.Equals(mappedEffort, inboundEffort, StringComparison.OrdinalIgnoreCase))
        {
            var oc = body.OutputConfig with { Effort = mappedEffort };
            body = body with { OutputConfig = oc };
            bodyChanged = true;
            log?.LogDebug("    EffortMap: '{Inbound}' → '{Mapped}'", inboundEffort, mappedEffort);
        }

        if (bodyChanged) ctx.Request.Body = body;

        if (use.Headers is { } h)
        {
            ApplyHeaders(ctx, h);
        }
    }

    private static void ApplyHeaders(BridgeContext<MessagesRequest> ctx, LocationHeaders h)
    {
        if (h.Set is { Count: > 0 } setMap)
        {
            foreach (var (name, value) in setMap)
            {
                if (string.Equals(name, "anthropic-beta", StringComparison.OrdinalIgnoreCase))
                {
                    // Split value into tokens and stage them as PendingBetaAdds;
                    // HeadersOutboundStage merges them with the inbound set and
                    // the derived set, then applies strip patterns. Splitting
                    // here means the user can write either a single token or a
                    // comma list with the same effect.
                    foreach (var token in SplitBetaTokens(value))
                    {
                        ctx.PendingBetaAdds.Add(token);
                    }
                }
                else
                {
                    // Whitelisted Copilot identity header (validated at startup).
                    // Stage it for CopilotHeaderFactory to honor at HTTP-build time.
                    ctx.CopilotHeaderOverrides[name] = value;
                }
            }
        }

        if (h.Remove is { Count: > 0 } removeList)
        {
            foreach (var entry in removeList)
            {
                // Token-level form: "anthropic-beta:context-1m-*". The colon
                // splits header name from the token pattern; only meaningful
                // for anthropic-beta today (other whitelisted headers are
                // single-value).
                var colon = entry.IndexOf(':');
                if (colon > 0)
                {
                    var hName = entry[..colon];
                    var pattern = entry[(colon + 1)..];
                    if (string.Equals(hName, "anthropic-beta", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.PendingBetaStrips.Add(pattern);
                    }
                    // Non-beta token-form removes don't have a use case today;
                    // RoutesValidator already rejected them at startup.
                }
                else
                {
                    // Whole-header remove on a Copilot identity header.
                    // Setting to null tells CopilotHeaderFactory to skip it.
                    ctx.CopilotHeaderOverrides[entry] = null;
                }
            }
        }
    }

    private static IEnumerable<string> SplitBetaTokens(string raw)
    {
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0) yield return part;
        }
    }
}

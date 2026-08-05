using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Shared, I/O-free route planner for messages and count-tokens. It mutates only
/// the supplied request-scoped context: normalize, apply the first Location,
/// validate/coerce the target profile, and select the backend.
/// </summary>
internal sealed class ModelRoutePlanner
{
    private readonly IModelRegistry _registry;
    private readonly ModelProfileCatalog _profiles;
    private readonly CodexModelProfileCatalog _codexProfiles;
    private readonly RoutesConfig _routes;
    private readonly OutboundBetaPolicyOptions _betaPolicy;
    private readonly ILogger<ModelRoutePlanner> _log;
    private readonly ILogger<ModelRouteResolverLog> _resolverLog;
    private readonly ILogger<ProfileAdjusterLog> _adjusterLog;

    public ModelRoutePlanner(
        IModelRegistry registry,
        ModelProfileCatalog profiles,
        CodexModelProfileCatalog codexProfiles,
        IOptions<RoutesConfig> routesOptions,
        IOptions<OutboundBetaPolicyOptions> betaPolicyOptions,
        ILogger<ModelRoutePlanner> log,
        ILogger<ModelRouteResolverLog> resolverLog,
        ILogger<ProfileAdjusterLog> adjusterLog)
    {
        _registry = registry;
        _profiles = profiles;
        _codexProfiles = codexProfiles;
        _routes = routesOptions.Value;
        _betaPolicy = betaPolicyOptions.Value;
        _log = log;
        _resolverLog = resolverLog;
        _adjusterLog = adjusterLog;
    }

    public RouteTarget Plan(BridgeContext<MessagesRequest> ctx)
    {
        var requested = ctx.Request.Body.Model;
        ctx.OriginalRequestedModel = requested;

        var canonical = CopilotModelRegistry.Normalize(requested);
        if (!string.Equals(requested, canonical, StringComparison.Ordinal))
            ctx.Request.Body = ctx.Request.Body with { Model = canonical };

        var (matchedLoc, locIndex) = ModelRouteResolver.Apply(ctx, _routes, _resolverLog);
        var afterRule = ctx.Request.Body.Model;
        var resolvedEarly = _registry.Resolve(afterRule);

        if (resolvedEarly is { Vendor: BackendVendor.CopilotResponses })
        {
            ValidateCodexProfile(requested, afterRule, matchedLoc, locIndex);
            ctx.Target = resolvedEarly;
            _log.LogDebug(
                "route planner: '{Requested}' → '{FinalModel}' target={Vendor}:{Endpoint} "
                + "(Responses; Anthropic profile skipped)",
                requested, afterRule, resolvedEarly.Vendor, resolvedEarly.Endpoint);
            return resolvedEarly;
        }

        var profile = ResolveAnthropicProfile(
            requested, afterRule, matchedLoc, locIndex);
        profile = ProfileAdjuster.Apply(
            ctx, profile, _profiles, _adjusterLog, _betaPolicy.GlobalStrip);

        var finalModel = ctx.Request.Body.Model;
        var resolved = _registry.Resolve(finalModel)
            ?? throw new InvalidOperationException(
                $"No backend route for model '{finalModel}'. Add the prefix to "
                + $"{nameof(CopilotModelRegistry)} (claude-/gpt-/o3-/o4-/gemini-).");

        ctx.Target = resolved;
        var rule = matchedLoc is null
            ? "—"
            : $"#{locIndex}{(matchedLoc.Note is null ? "" : $" '{matchedLoc.Note}'")}";
        _log.LogDebug(
            "route planner: '{Requested}' → '{FinalModel}' profile={ProfileId} "
            + "target={Vendor}:{Endpoint} location={Location}",
            requested, finalModel, profile.CanonicalId,
            resolved.Vendor, resolved.Endpoint, rule);
        return resolved;
    }

    private void ValidateCodexProfile(
        string requested, string resolved, RouteLocation? location, int index)
    {
        if (_codexProfiles.Get(resolved) is not null) return;

        var nearest = _codexProfiles.GetNearest(
            resolved, out var matchedId, out var score);
        if (nearest is null)
        {
            var ex = Unknown(
                requested, resolved, location, index,
                _codexProfiles.KnownIds, matchedId, score);
            _log.LogError("{Message}", ex.Message);
            throw ex;
        }

        _log.LogWarning(
            "route planner: no exact Codex profile for '{Resolved}' — fuzzy-matched "
            + "to '{Matched}' (jaccard={Score:F2}) and borrowing its effort/tool rules",
            resolved, matchedId, score);
    }

    private ModelProfile ResolveAnthropicProfile(
        string requested, string resolved, RouteLocation? location, int index)
    {
        var profile = _profiles.Get(resolved);
        if (profile is not null) return profile;

        var nearest = _profiles.GetNearest(resolved, out var matchedId, out var score);
        if (nearest is null)
        {
            var ex = Unknown(
                requested, resolved, location, index,
                _profiles.KnownIds, matchedId, score);
            _log.LogError("{Message}", ex.Message);
            throw ex;
        }

        _log.LogWarning(
            "route planner: no exact profile for '{Resolved}' — fuzzy-matched to "
            + "'{Matched}' (jaccard={Score:F2}) and borrowing its wire contract",
            resolved, matchedId, score);
        return nearest with
        {
            EffortOnUnsupported = EffortHandling.Strip,
            EffortToVariant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static UnknownModelException Unknown(
        string requested,
        string resolved,
        RouteLocation? location,
        int index,
        IReadOnlyList<string> known,
        string matchedId,
        double score) =>
        new(
            requestedModel: requested,
            resolvedModel: resolved,
            appliedLocation: location,
            appliedLocationIndex: location is null ? null : index,
            knownProfiles: known,
            bestCandidate: matchedId.Length > 0 ? matchedId : null,
            bestScore: score);
}

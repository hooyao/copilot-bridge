using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Resolves model + target profile + body shape in a single stage. See the
/// class-level docs of <c>BridgePipelines</c> assembly point for the wider
/// pipeline picture. Must run early — every later stage that reads
/// <c>ctx.Target</c> or the transformed body depends on this stage having
/// completed.
/// </summary>
internal sealed class ModelRouterStage : IRequestStage<MessagesRequest>
{
    private readonly IModelRegistry _registry;
    private readonly Routing.ModelProfileCatalog _profiles;
    private readonly Routing.CodexModelProfileCatalog _codexProfiles;
    private readonly Routing.RoutesConfig _routes;
    private readonly OutboundBetaPolicyOptions _betaPolicy;
    private readonly ILogger<ModelRouterStage> _log;
    private readonly ILogger<ModelRouteResolverLog> _resolverLog;
    private readonly ILogger<ProfileAdjusterLog> _adjusterLog;

    public ModelRouterStage(
        IModelRegistry registry,
        Routing.ModelProfileCatalog profiles,
        Routing.CodexModelProfileCatalog codexProfiles,
        IOptions<Routing.RoutesConfig> routesOptions,
        IOptions<OutboundBetaPolicyOptions> betaPolicyOptions,
        ILogger<ModelRouterStage> log,
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

    public string Name => "ModelRouter";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var requested = ctx.Request.Body.Model;
        // Stash the original, pre-normalize id so the response pipeline can
        // restore it on the way back out (see ResponseModelRewriteStage).
        ctx.OriginalRequestedModel = requested;

        // 1. Normalize.
        var canonical = Routing.CopilotModelRegistry.Normalize(requested);
        if (!string.Equals(requested, canonical, StringComparison.Ordinal))
        {
            ctx.Request.Body = ctx.Request.Body with { Model = canonical };
        }

        // 2. User location (nginx-style first-match-wins).
        var (matchedLoc, locIndex) = Routing.ModelRouteResolver.Apply(ctx, _routes, _resolverLog);
        var afterRule = ctx.Request.Body.Model;

        // 2b. Vendor dispatch on the post-rule id. Do this BEFORE the Anthropic
        //     profile lookup: a Codex/Responses model (gpt-*) has no entry in the
        //     Anthropic ModelProfileCatalog and must NOT run ProfileAdjuster
        //     (thinking-shape coercion, effort-to-variant, mid-conv-system fold
        //     are all Anthropic-specific). Its per-model effort coercion lives in
        //     T2 (CodexModelProfileCatalog), applied in the Responses strategy.
        //     The Anthropic profile machinery (steps 3-4) is for the /v1/messages
        //     backend only.
        var resolvedEarly = _registry.Resolve(afterRule);
        if (resolvedEarly is { Vendor: BackendVendor.CopilotResponses })
        {
            // The Anthropic ModelProfileCatalog is skipped here (gpt-* has no
            // entry), but the Codex side still needs a profile: T2's per-model
            // CoerceEffort + custom-tool drop are driven by CodexModelProfileCatalog.
            // Assert one exists. Without this, a routing/catalog drift (id in
            // CopilotModelRegistry.ResponsesModelIds but absent from the Codex
            // catalog) would silently disable every coercion — effort passes
            // through unclamped (Copilot 400s it), custom tools aren't dropped
            // (Copilot 500s). Surface it as the same actionable 400 the Anthropic
            // miss raises rather than a downstream upstream error.
            if (_codexProfiles.Get(afterRule) is null)
            {
                var codexEx = new Routing.UnknownModelException(
                    requestedModel: requested,
                    resolvedModel: afterRule,
                    appliedLocation: matchedLoc,
                    appliedLocationIndex: matchedLoc is null ? null : locIndex,
                    knownProfiles: _codexProfiles.KnownIds);
                _log.LogError("{Message}", codexEx.Message);
                throw codexEx;
            }

            ctx.Target = resolvedEarly;
            _log.LogDebug(
                "stage {Name}: '{Requested}' → '{FinalModel}'  target={Vendor}:{Endpoint}  (Codex/Responses — Anthropic profile skipped)",
                Name, requested, afterRule, resolvedEarly.Vendor, resolvedEarly.Endpoint);
            return Task.CompletedTask;
        }

        // 3. Profile lookup. Miss = hard error with actionable diagnostics.
        var profile = _profiles.Get(afterRule);
        if (profile is null)
        {
            var ex = new Routing.UnknownModelException(
                requestedModel: requested,
                resolvedModel: afterRule,
                appliedLocation: matchedLoc,
                appliedLocationIndex: matchedLoc is null ? null : locIndex,
                knownProfiles: _profiles.KnownIds);
            _log.LogError("{Message}", ex.Message);
            throw ex;
        }

        // 4. Profile-driven body adjustment. May switch profile via variant routing.
        profile = Routing.ProfileAdjuster.Apply(ctx, profile, _profiles, _adjusterLog, _betaPolicy.GlobalStrip);

        // 5. Vendor/endpoint dispatch on the final model id.
        var finalModel = ctx.Request.Body.Model;
        var resolved = _registry.Resolve(finalModel)
            ?? throw new InvalidOperationException(
                $"No backend route for model '{finalModel}'. Add the prefix to "
                + $"{nameof(Routing.CopilotModelRegistry)} (claude-/gpt-/o3-/o4-/gemini-).");

        ctx.Target = resolved;

        var ruleSummary = matchedLoc is null ? "—" : $"#{locIndex}{(matchedLoc.Note is null ? "" : $" '{matchedLoc.Note}'")}";
        _log.LogDebug(
            "stage {Name}: '{Requested}' → '{FinalModel}'  profile={ProfileId}  target={Vendor}:{Endpoint}  location={Location}",
            Name, requested, finalModel, profile.CanonicalId, ctx.Target.Vendor, ctx.Target.Endpoint, ruleSummary);
        return Task.CompletedTask;
    }
}

using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Options;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Resolves model + target profile + body shape in a single stage. The flow:
/// <list type="number">
///   <item><b>Normalize</b> the inbound model id via
///         <see cref="CopilotModelRegistry.Normalize"/>.</item>
///   <item><b>Apply the first matching user rule</b> from
///         <see cref="RoutesConfig.Rules"/>. Rules express only model
///         redirects — anything else is decided by the target profile.</item>
///   <item><b>Look up the target profile</b> in
///         <see cref="ModelProfileCatalog"/>. A miss throws
///         <see cref="UnknownModelException"/>, which the endpoint converts
///         into a 400 + Anthropic-format error body so the user sees what to
///         configure.</item>
///   <item><b>Adjust the body</b> via <see cref="ProfileAdjuster"/> — mechanical,
///         profile-driven coercion of effort / thinking / mid-conv system /
///         budget cap. The adjuster can switch the active profile when it
///         routes an effort to a sized variant model id.</item>
///   <item><b>Resolve</b> the (possibly-rewritten) model id to a
///         <see cref="RouteTarget"/>.</item>
/// </list>
/// Must run early — every later stage that reads <c>ctx.Target</c> or the
/// transformed body depends on this stage having completed.
/// </summary>
internal sealed class ModelRouterStage : IRequestStage<MessagesRequest>
{
    private readonly IModelRegistry _registry;
    private readonly ModelProfileCatalog _profiles;
    private readonly RoutesConfig _routes;

    public ModelRouterStage(
        IModelRegistry registry,
        ModelProfileCatalog profiles,
        IOptions<RoutesConfig> routesOptions)
    {
        _registry = registry;
        _profiles = profiles;
        _routes = routesOptions.Value;
    }

    public string Name => "ModelRouter";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var requested = ctx.Request.Body.Model;

        // 1. Normalize.
        var canonical = CopilotModelRegistry.Normalize(requested);
        if (!string.Equals(requested, canonical, StringComparison.Ordinal))
        {
            ctx.Request.Body = ctx.Request.Body with { Model = canonical };
        }

        // 2. User location (nginx-style first-match-wins).
        var (matchedLoc, locIndex) = ModelRouteResolver.Apply(ctx, _routes);
        var afterRule = ctx.Request.Body.Model;

        // 3. Profile lookup. Miss = hard error with actionable diagnostics.
        var profile = _profiles.Get(afterRule);
        if (profile is null)
        {
            // Log the full diagnostic to Serilog before throwing — the
            // endpoint will surface a 400 to the client, but the operator's
            // log gets the full story (matched location, known profiles, etc.).
            var ex = new UnknownModelException(
                requestedModel: requested,
                resolvedModel: afterRule,
                appliedLocation: matchedLoc,
                appliedLocationIndex: matchedLoc is null ? null : locIndex,
                knownProfiles: _profiles.KnownIds);
            Log.Error(ex.Message);
            throw ex;
        }

        // 4. Profile-driven body adjustment. May switch profile via variant routing.
        profile = ProfileAdjuster.Apply(ctx, profile, _profiles);

        // 5. Vendor/endpoint dispatch on the final model id.
        var finalModel = ctx.Request.Body.Model;
        var resolved = _registry.Resolve(finalModel)
            ?? throw new InvalidOperationException(
                $"No backend route for model '{finalModel}'. Add the prefix to "
                + $"{nameof(CopilotModelRegistry)} (claude-/gpt-/o3-/o4-/gemini-).");

        ctx.Target = resolved;

        var ruleSummary = matchedLoc is null ? "—" : $"#{locIndex}{(matchedLoc.Note is null ? "" : $" '{matchedLoc.Note}'")}";
        Log.Debug($"stage {Name}: '{requested}' → '{finalModel}'  profile={profile.CanonicalId}  target={ctx.Target.Vendor}:{ctx.Target.Endpoint}  location={ruleSummary}");
        return Task.CompletedTask;
    }
}

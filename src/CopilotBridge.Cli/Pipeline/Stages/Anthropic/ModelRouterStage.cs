using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Options;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Resolves model + variant + body shape in a single stage:
/// <list type="number">
///   <item>Normalize <c>ctx.Request.Body.Model</c> via
///         <see cref="CopilotModelRegistry.Normalize"/> so subsequent rules
///         match the canonical form (dotted, no date suffix).</item>
///   <item>Apply the two-phase <see cref="ModelRouteResolver"/>:
///         shape rewrites from <c>appsettings.json</c> first
///         (haiku→enabled, opus-4.7→adaptive, etc.), then the
///         capability-driven effort routing in
///         <see cref="CopilotModelRegistry"/>.</item>
///   <item>Hand the (possibly-rewritten) body's model to
///         <see cref="IModelRegistry.Resolve"/> for vendor + endpoint, and
///         set <c>ctx.Target</c> for the strategy registry.</item>
/// </list>
/// Must run early — every later stage that reads <c>ctx.Target</c> or the
/// transformed body depends on this stage having completed.
/// </summary>
internal sealed class ModelRouterStage : IRequestStage<MessagesRequest>
{
    private readonly IModelRegistry _registry;
    private readonly RoutesConfig _routes;

    public ModelRouterStage(IModelRegistry registry, IOptions<RoutesConfig> routesOptions)
    {
        _registry = registry;
        _routes = routesOptions.Value;
    }

    public string Name => "ModelRouter";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var requested = ctx.Request.Body.Model;
        var canonical = CopilotModelRegistry.Normalize(requested);
        if (!string.Equals(requested, canonical, StringComparison.Ordinal))
        {
            ctx.Request.Body = ctx.Request.Body with { Model = canonical };
        }

        var shape = ModelRouteResolver.Apply(ctx, _routes, _registry);

        var finalModel = ctx.Request.Body.Model;
        var resolved = _registry.Resolve(finalModel)
            ?? throw new InvalidOperationException(
                $"No backend route for model '{finalModel}'. Add the prefix to "
                + $"{nameof(CopilotModelRegistry)} (claude-/gpt-/o3-/o4-/gemini-).");

        ctx.Target = resolved;

        Log.Debug($"stage {Name}: '{requested}' → '{finalModel}'  target={ctx.Target.Vendor}:{ctx.Target.Endpoint}  shapeRule={(shape?.Note ?? "—")}");
        return Task.CompletedTask;
    }
}

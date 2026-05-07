using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Resolves <c>ctx.Request.Body.Model</c> through the
/// <see cref="IModelRegistry"/>, populates <c>ctx.Target</c>, and rewrites the
/// body's model id to whatever the registry decided (after normalization /
/// aliasing). Must run early in the request pipeline — every later stage
/// (<c>HeadersOutboundStage</c> in particular) reads <c>ctx.Target</c>.
/// </summary>
internal sealed class ModelRouterStage : IRequestStage<MessagesRequest>
{
    private readonly IModelRegistry _registry;

    public ModelRouterStage(IModelRegistry registry)
    {
        _registry = registry;
    }

    public string Name => "ModelRouter";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var requested = ctx.Request.Body.Model;
        var resolved = _registry.Resolve(requested);
        if (resolved is null)
        {
            throw new InvalidOperationException(
                $"No backend route for model '{requested}'. The model is unknown to the registry "
                + "and cannot be aliased to anything available on Copilot.");
        }

        DiagTracer.Log($"stage {Name}: requested='{requested}' resolved='{resolved.ModelId}' target={resolved.Vendor}:{resolved.Endpoint}");

        if (!string.Equals(requested, resolved.ModelId, StringComparison.Ordinal))
        {
            ctx.Request.Body = ctx.Request.Body with { Model = resolved.ModelId };
        }
        ctx.Target = resolved;
        return Task.CompletedTask;
    }
}

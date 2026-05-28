namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Resolves a client-supplied model id (post any client-side adapter
/// translation) to a <see cref="RouteTarget"/>, and applies per-model
/// effort-routing capability. The registry is the single source of truth
/// for "what does Copilot's variant of model X expect": it knows the live
/// Copilot model list, vendor + endpoint dispatch by prefix, and which
/// models route requests by <c>output_config.effort</c> to a sized variant.
/// </summary>
internal interface IModelRegistry
{
    /// <summary>
    /// Returns the resolved target, or null if no mapping exists. Callers
    /// should respond with a clear "model X not available" 400 in that case.
    /// </summary>
    RouteTarget? Resolve(string requestedModelId);

    /// <summary>
    /// Apply per-model effort-routing capability. Returns the
    /// (possibly-suffixed) model id and whether the inbound
    /// <c>output_config.effort</c> field should be stripped from the
    /// outbound body. The default for unregistered models is "strip the
    /// field, do not change the model" — most Copilot models are base
    /// models that reject the effort field outright, and the safer
    /// behavior is to drop it than risk a 400.
    /// </summary>
    (string Model, bool StripEffort) ApplyEffortRouting(string normalizedModelId, string? effort);
}

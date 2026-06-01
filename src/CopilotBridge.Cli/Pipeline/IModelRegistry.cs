namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Resolves a normalized model id to a backend <see cref="RouteTarget"/>
/// (vendor + endpoint). Capability data — which efforts a model accepts, which
/// thinking shapes work, etc. — lives in
/// <see cref="Routing.ModelProfileCatalog"/> instead, since profiles are
/// playground-derived facts about Copilot's API surface, not routing concerns.
/// </summary>
internal interface IModelRegistry
{
    /// <summary>
    /// Returns the resolved target, or null if the prefix doesn't match a
    /// known vendor family (claude-/gpt-/o3-/o4-/gemini-).
    /// </summary>
    RouteTarget? Resolve(string requestedModelId);
}

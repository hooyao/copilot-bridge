namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// Resolves a client-supplied model id (post any client-side adapter
/// translation) to a <see cref="RouteTarget"/>. The registry knows the live
/// Copilot model list plus a static alias table for graceful degradation when
/// a client requests a model that doesn't exist on Copilot yet (e.g.
/// <c>claude-opus-4.8</c> → <c>claude-opus-4.7</c>).
/// </summary>
internal interface IModelRegistry
{
    /// <summary>
    /// Returns the resolved target, or null if no mapping exists. Callers
    /// should respond with a clear "model X not available" 400 in that case.
    /// </summary>
    RouteTarget? Resolve(string requestedModelId);
}

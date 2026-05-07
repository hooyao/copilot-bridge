namespace CopilotBridge.Cli.Pipeline;

/// <summary>
/// The resolved upstream destination for a request: which Copilot endpoint
/// shape, and which backend-side model id to use after alias resolution.
/// Populated by <c>ModelRouterStage</c>; consumed by the strategy registry
/// to pick an <see cref="Strategies.IUpstreamStrategy{TBody}"/>.
/// </summary>
internal sealed record RouteTarget(BackendVendor Vendor, string Endpoint, string ModelId);

/// <summary>
/// The Copilot backend shapes. Note Copilot does not expose a Gemini-shape
/// endpoint — Gemini models are served via <see cref="CopilotOpenAi"/>.
/// </summary>
internal enum BackendVendor
{
    /// <summary>POST <c>/v1/messages</c> — Anthropic Messages API shape.</summary>
    CopilotAnthropic,

    /// <summary>POST <c>/chat/completions</c> — OpenAI Chat Completions shape (also serves Gemini models).</summary>
    CopilotOpenAi,

    /// <summary>POST <c>/responses</c> — OpenAI Responses API shape (o-series, gpt-5).</summary>
    CopilotResponses,
}

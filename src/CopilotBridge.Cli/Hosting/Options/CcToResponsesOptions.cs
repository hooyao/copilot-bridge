namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>Pipeline:CcToResponses</c>. Controls compatibility policies that
/// apply only while translating a Claude Code request to a Copilot Responses
/// backend; native Anthropic passthrough and native Codex requests do not consult
/// these options.
/// </summary>
internal sealed class CcToResponsesOptions
{
    /// <summary>
    /// When true, omit Claude Code's exact <c>Agent</c> tool from a sub-agent's
    /// translated Responses request. Root agents retain delegation. Default true
    /// because GPT can otherwise recursively fan out a bounded-depth agent tree into
    /// hundreds of concurrent requests.
    /// </summary>
    public bool PreventRecursiveAgentDelegation { get; set; } = true;
}

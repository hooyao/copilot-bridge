namespace CopilotBridge.Cli.Models.Anthropic.Common;

/// <summary>
/// Mirrors <c>BetaCacheControlEphemeral</c> in @anthropic-ai/sdk. Marks a content
/// block as a prompt-cache breakpoint. <c>scope</c> exists in some Claude Code
/// payloads on system blocks but Copilot rejects it — preprocessing strips it.
/// </summary>
internal sealed record CacheControl
{
    public string Type { get; init; } = "ephemeral";

    /// <summary><c>"5m"</c> (default) or <c>"1h"</c>.</summary>
    public string? Ttl { get; init; }
}

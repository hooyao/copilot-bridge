using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Common;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaTool</c>. Claude Code 2.1.146 can ship server tools
/// (notably <c>web_search_*</c>) via <c>tools[]</c> when the user enables the
/// WebSearch built-in. <see cref="Type"/> distinguishes custom tools (null /
/// omitted / <c>"custom"</c>) from server tools (<c>web_search_20250305</c>,
/// etc.). The bridge rejects server tools the Copilot upstream doesn't
/// support — see <c>ClaudeCodeMessagesEndpoint</c>.
/// </summary>
internal sealed record Tool
{
    public required string Name { get; init; }

    /// <summary>
    /// Server-tool discriminator. Null / "custom" = user-defined function tool
    /// (the normal Claude Code case). Non-null values like
    /// <c>web_search_20250305</c> mark Anthropic server tools — the bridge
    /// intercepts these so they don't reach Copilot (which rejects them).
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Required for custom tools, optional / absent for server tools (the
    /// Anthropic API decides the schema for those internally). Made optional
    /// here so a request with <c>{"type": "web_search_20250305", "name": "web_search"}</c>
    /// deserializes without failing — we filter such tools out at the endpoint.
    /// </summary>
    public InputSchema? InputSchema { get; init; }
    public string? Description { get; init; }
    public CacheControl? CacheControl { get; init; }

    /// <summary>VS Code Copilot's tool-search defer-loading hint.</summary>
    public bool? DeferLoading { get; init; }

    public bool? Strict { get; init; }
}

/// <summary>
/// Mirrors <c>BetaTool.InputSchema</c>. The schema is JSON-Schema 2020-12; we keep
/// it as a raw <see cref="JsonElement"/> so callers can supply arbitrary shapes.
/// </summary>
internal sealed record InputSchema
{
    public string Type { get; init; } = "object";
    public JsonElement? Properties { get; init; }
    public IReadOnlyList<string>? Required { get; init; }
}

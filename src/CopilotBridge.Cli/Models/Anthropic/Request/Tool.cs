using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Common;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaTool</c> (custom-tool variant only — Claude Code does not use
/// built-in server tools like web_search / code_execution / computer_use). The
/// <c>type</c> field is omitted (defaults to <c>"custom"</c>).
/// </summary>
internal sealed record Tool
{
    public required string Name { get; init; }
    public required InputSchema InputSchema { get; init; }
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

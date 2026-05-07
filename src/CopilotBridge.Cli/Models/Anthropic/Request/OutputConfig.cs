using System.Text.Json;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaOutputConfig</c>. Pairs with <see cref="ThinkingConfigAdaptive"/>
/// to choose effort level on opus-4.7 family models (research §15.3).
/// </summary>
internal sealed record OutputConfig
{
    /// <summary>One of <c>low | medium | high | xhigh | max</c>.</summary>
    public string? Effort { get; init; }

    public JsonOutputFormat? Format { get; init; }

    public TokenTaskBudget? TaskBudget { get; init; }
}

/// <summary>Mirrors <c>BetaJSONOutputFormat</c>.</summary>
internal sealed record JsonOutputFormat
{
    public string Type { get; init; } = "json_schema";
    public JsonElement Schema { get; init; }
}

/// <summary>Mirrors <c>BetaTokenTaskBudget</c>.</summary>
internal sealed record TokenTaskBudget
{
    public string Type { get; init; } = "tokens";
    public int Total { get; init; }
    public int? Remaining { get; init; }
}

using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaThinkingConfigParam</c>. Per research §15.3 the bridge rewrites
/// this per target-model family — haiku-* must use <see cref="ThinkingConfigEnabled"/>,
/// opus-4.7-* must use <see cref="ThinkingConfigAdaptive"/>, sonnet accepts both.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ThinkingConfigEnabled), "enabled")]
[JsonDerivedType(typeof(ThinkingConfigDisabled), "disabled")]
[JsonDerivedType(typeof(ThinkingConfigAdaptive), "adaptive")]
internal abstract record ThinkingConfig;

internal sealed record ThinkingConfigEnabled : ThinkingConfig
{
    /// <summary>Token budget for internal reasoning. Must be ≥1024 and &lt; max_tokens.</summary>
    public int BudgetTokens { get; init; }

    /// <summary><c>"summarized"</c> (default) or <c>"omitted"</c>.</summary>
    public string? Display { get; init; }
}

internal sealed record ThinkingConfigDisabled : ThinkingConfig;

internal sealed record ThinkingConfigAdaptive : ThinkingConfig
{
    /// <summary><c>"summarized"</c> (default) or <c>"omitted"</c>.</summary>
    public string? Display { get; init; }
}

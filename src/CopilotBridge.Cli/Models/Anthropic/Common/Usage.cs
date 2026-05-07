namespace CopilotBridge.Cli.Models.Anthropic.Common;

/// <summary>
/// Mirrors <c>BetaUsage</c>. Copilot's Bedrock backend populates the nested
/// <see cref="CacheCreation"/> object alongside the flat token counters.
/// </summary>
internal sealed record Usage
{
    public CacheCreation? CacheCreation { get; init; }
    public int? CacheCreationInputTokens { get; init; }
    public int? CacheReadInputTokens { get; init; }
    public string? InferenceGeo { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public string? ServiceTier { get; init; }
    public string? Speed { get; init; }
}

/// <summary>
/// Mirrors <c>BetaCacheCreation</c>. Per-TTL breakdown of cache-creation tokens;
/// Copilot reports both buckets even when only one is in use.
/// </summary>
internal sealed record CacheCreation
{
    public int Ephemeral1hInputTokens { get; init; }
    public int Ephemeral5mInputTokens { get; init; }
}

/// <summary>
/// Mirrors <c>BetaMessageDeltaUsage</c>. Cumulative usage emitted on
/// <c>message_delta</c> stream events; differs from <see cref="Usage"/> in that
/// most counters are nullable (only deltas since the last event).
/// </summary>
internal sealed record MessageDeltaUsage
{
    public int? CacheCreationInputTokens { get; init; }
    public int? CacheReadInputTokens { get; init; }
    public int? InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

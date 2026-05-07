namespace CopilotBridge.Cli.Models.Anthropic.Common;

/// <summary>
/// Mirrors <c>BetaStopReason</c>. String constants instead of an enum because the
/// JSON wire form is the union of string literals; Claude Code maps these to
/// finish reasons in <c>messagesApi.ts</c>.
/// </summary>
internal static class StopReason
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string StopSequence = "stop_sequence";
    public const string ToolUse = "tool_use";
    public const string PauseTurn = "pause_turn";
    public const string Compaction = "compaction";
    public const string Refusal = "refusal";
    public const string ModelContextWindowExceeded = "model_context_window_exceeded";
}

/// <summary>
/// Mirrors <c>BetaRefusalStopDetails</c>. Present on <c>BetaMessage.stop_details</c>
/// when <c>stop_reason="refusal"</c>; null otherwise.
/// </summary>
internal sealed record RefusalStopDetails
{
    public string Type { get; init; } = "refusal";
    /// <summary>One of <c>"cyber"</c>, <c>"bio"</c>, or null.</summary>
    public string? Category { get; init; }
    public string? Explanation { get; init; }
}

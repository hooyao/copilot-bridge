namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// The retryable error a response detector raises when it aborts a response —
/// shared by the leak guard (tool-call / control-envelope leak), the runaway guard
/// (volume / repetition), and the tool-input validator. Selects both the Anthropic
/// <c>error.type</c> string and (buffered delivery) the HTTP status.
/// </summary>
/// <remarks>
/// <see cref="OverloadedError"/> is the usual default: Claude Code's retry logic
/// (<c>withRetry.ts</c> <c>is529Error</c>) matches the literal
/// <c>"type":"overloaded_error"</c> mid-stream and retries; 3 consecutive on a
/// non-custom Opus model trigger an opus→Sonnet fallback — a fitting backstop for
/// a poisoned session. <see cref="ApiError"/> maps to a generic
/// <c>api_error</c>/500; Claude Code retries 5xx too, but mid-stream
/// classification is less certain than the overloaded string match.
/// </remarks>
internal enum ResponseDetectionSignal
{
    OverloadedError = 0,
    ApiError = 1,
}

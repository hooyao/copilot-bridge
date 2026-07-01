namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// The error the guard raises when it detects a tool-call leak. Selects both the
/// Anthropic <c>error.type</c> string and (buffered delivery) the HTTP status.
/// </summary>
/// <remarks>
/// <see cref="OverloadedError"/> is the default: Claude Code's retry logic
/// (<c>withRetry.ts</c> <c>is529Error</c>) matches the literal
/// <c>"type":"overloaded_error"</c> mid-stream and retries; 3 consecutive on a
/// non-custom Opus model trigger an opus→Sonnet fallback — a fitting backstop for
/// a poisoned session. <see cref="ApiError"/> maps to a generic
/// <c>api_error</c>/500; Claude Code retries 5xx too, but mid-stream
/// classification is less certain than the overloaded string match.
/// </remarks>
internal enum ToolLeakSignal
{
    OverloadedError = 0,
    ApiError = 1,
}

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Pipeline:ToolLeakGuard</c>.
/// Controls <see cref="Pipeline.Response.Detection.ToolLeakDetector"/>, which
/// detects a Copilot-served Claude model leaking a tool call as literal
/// <c>&lt;invoke name="X"&gt;…&lt;/invoke&gt;</c> XML inside a text/thinking
/// block (rather than a real <c>tool_use</c> block) and forces the client to
/// retry the turn cleanly.
/// </summary>
/// <remarks>
/// Detection is structural and requires ALL of: (1) a single block with a CLOSED,
/// balanced <c>&lt;invoke&gt;…&lt;/invoke&gt;</c> containing ≥1 closed
/// <c>&lt;parameter&gt;</c>; (2) the tool name is in the request's
/// <c>tools[]</c>; (3) it is NOT inside a markdown code fence. It does NOT key off
/// the drifting prefix token (<c>court</c>/<c>call</c>), <c>stop_reason</c>, or a
/// bare unbalanced <c>&lt;invoke</c>.
/// </remarks>
internal sealed class ToolLeakGuardOptions
{
    /// <summary>Master switch. When false the detector is never created — no
    /// scanning, no automaton allocation. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// true (default): keep streaming, inject the error mid-stream on detection
    /// (TTFT preserved; only dirty turns pay). false: buffer the whole response
    /// and emit a real HTTP status on a dirty turn — sacrifices streaming for ALL
    /// requests, not just dirty ones.
    /// </summary>
    public bool PreserveStream { get; set; } = true;

    /// <summary>Which error to raise. Default <see cref="ToolLeakSignal.OverloadedError"/>.</summary>
    public ToolLeakSignal Signal { get; set; } = ToolLeakSignal.OverloadedError;

    /// <summary>Also scan <c>thinking</c> blocks, not just <c>text</c>. Default true.</summary>
    public bool ScanThinking { get; set; } = true;

    /// <summary>
    /// Content-retention cap for buffering detectors only (0 = unbounded). The
    /// tool-leak detector is a single-pass automaton that retains no content, so
    /// this does not affect it; it exists for a future JSON-repair-style detector
    /// that must hold a block's bytes. Default 10000.
    /// </summary>
    public int MaxScanChars { get; set; } = 10000;
}

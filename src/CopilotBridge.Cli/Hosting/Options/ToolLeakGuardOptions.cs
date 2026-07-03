namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// The error the guard raises when it detects a leak — a tool-call leak or a
/// control-envelope leak. Selects both the Anthropic <c>error.type</c> string and
/// (buffered delivery) the HTTP status.
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
/// Bound from <c>appsettings.json</c> section
/// <c>Pipeline:Detectors:ToolLeakGuard</c>. Controls <see cref="Pipeline.Response.Detection.ToolLeakDetector"/>, which
/// detects two families of leaks from a Copilot-served Claude model: a tool call
/// leaked as literal <c>&lt;invoke name="X"&gt;…&lt;/invoke&gt;</c> XML (rather
/// than a real <c>tool_use</c> block), and a Claude Code control/event envelope
/// (<c>&lt;task-notification&gt;</c>, <c>&lt;teammate-message&gt;</c>,
/// <c>&lt;channel&gt;</c>, <c>&lt;cross-session-message&gt;</c>,
/// <c>&lt;tick&gt;</c>) leaked as literal text inside a text/thinking block. On
/// either it forces the client to retry the turn cleanly.
/// </summary>
/// <remarks>
/// Detection is structural and shape-gated. For a tool-call leak it requires ALL
/// of: (1) a single block with a CLOSED, balanced
/// <c>&lt;invoke&gt;…&lt;/invoke&gt;</c> containing ≥1 closed
/// <c>&lt;parameter&gt;</c>; (2) the tool name is in the request's
/// <c>tools[]</c>; (3) it is NOT inside a markdown code fence. It does NOT key off
/// the drifting prefix token (<c>court</c>/<c>call</c>), <c>stop_reason</c>, or a
/// bare unbalanced <c>&lt;invoke</c>. For a control envelope it requires the
/// envelope to be CLOSED with its required child/attribute present and non-empty
/// (e.g. <c>&lt;task-notification&gt;</c> needs a closed <c>&lt;task-id&gt;</c>
/// plus a <c>&lt;summary&gt;</c>/<c>&lt;status&gt;</c>/<c>&lt;output-file&gt;</c>
/// child; <c>&lt;channel&gt;</c> needs a non-empty <c>source</c> and is
/// distinguished from the sibling <c>&lt;channel-message&gt;</c>), and NOT inside a
/// code fence.
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

    /// <summary>
    /// Per-signature on/off switches. Each leak "signature" (the leaked
    /// <c>&lt;invoke&gt;</c> tool call, or one of the Claude Code control
    /// envelopes) can be disabled independently while the rest of the guard stays
    /// active. All default on.
    /// </summary>
    /// <remarks>
    /// <para>Purpose: clear a false positive without turning off the whole guard.
    /// If the user is legitimately discussing this markup with the model — e.g.
    /// asking how Anthropic tool-use or a <c>&lt;task-notification&gt;</c> works and
    /// the model echoes a sample — the guard can misfire. The name of the tripped
    /// signature and the exact key to disable it are surfaced in both the retry
    /// error and the server WARNING log, so the user knows which switch to flip.</para>
    /// <para><b>Applied at startup — a restart is required after changing any
    /// switch.</b> The detector is a scoped DI service that reads these options via
    /// <c>IOptionsSnapshot</c> (re-bound per request) and recomputes its enabled
    /// signatures on every request, so the only reason a config edit needs a restart
    /// is that <c>appsettings.json</c> is registered with <c>reloadOnChange:false</c>
    /// in <c>BridgeConfigurationExtensions</c>. Flipping that single flag to
    /// <c>true</c> would make a toggled switch take effect on the next request with
    /// no change to the detector.</para>
    /// </remarks>
    public ToolLeakSignaturesOptions Signatures { get; set; } = new();
}

/// <summary>
/// Per-signature toggles for the response-leak detector, bound from
/// <c>Pipeline:Detectors:ToolLeakGuard:Signatures</c>. Each property is a single
/// leak signature; set one to <c>false</c> to stop the guard tripping on that
/// shape while leaving the others active. All default <c>true</c>.
/// </summary>
/// <remarks>
/// Use this to clear a false positive: if the model is legitimately echoing this
/// markup (say the user asked how Claude Code's tool-use or task-notification
/// protocol works) and a sample gets caught, disable just that one signature. A
/// restart is required for a change to take effect — config is read at startup.
/// </remarks>
internal sealed class ToolLeakSignaturesOptions
{
    /// <summary>Leaked <c>&lt;invoke name="X"&gt;…&lt;/invoke&gt;</c> tool call. Default true.</summary>
    public bool Invoke { get; set; } = true;

    /// <summary>Leaked <c>&lt;task-notification&gt;</c> envelope. Default true.</summary>
    public bool TaskNotification { get; set; } = true;

    /// <summary>Leaked <c>&lt;teammate-message&gt;</c> envelope. Default true.</summary>
    public bool TeammateMessage { get; set; } = true;

    /// <summary>Leaked <c>&lt;channel&gt;</c> envelope. Default true.</summary>
    public bool Channel { get; set; } = true;

    /// <summary>Leaked <c>&lt;cross-session-message&gt;</c> envelope. Default true.</summary>
    public bool CrossSessionMessage { get; set; } = true;

    /// <summary>Leaked <c>&lt;tick&gt;</c> envelope. Default true.</summary>
    public bool Tick { get; set; } = true;

    /// <summary>
    /// Whether the given signature id (kebab-case, from
    /// <see cref="Pipeline.Response.Detection.LeakSignatures"/>) is enabled. The
    /// single id→flag mapping: callers derive the enabled set by filtering
    /// <see cref="Pipeline.Response.Detection.LeakSignatures.All"/> through this,
    /// so a new signature is wired by adding one <c>case</c> here (a
    /// <see cref="LeakSignatures"/> id with no case throws, caught by a test) rather
    /// than by editing a separate hand-maintained list. Throws on an unknown id so a
    /// typo fails loudly instead of silently disabling a signature.
    /// </summary>
    public bool IsEnabled(string signatureId) => signatureId switch
    {
        Pipeline.Response.Detection.LeakSignatures.Invoke => Invoke,
        Pipeline.Response.Detection.LeakSignatures.TaskNotification => TaskNotification,
        Pipeline.Response.Detection.LeakSignatures.TeammateMessage => TeammateMessage,
        Pipeline.Response.Detection.LeakSignatures.Channel => Channel,
        Pipeline.Response.Detection.LeakSignatures.CrossSessionMessage => CrossSessionMessage,
        Pipeline.Response.Detection.LeakSignatures.Tick => Tick,
        _ => throw new System.ArgumentOutOfRangeException(
            nameof(signatureId), signatureId, "Unknown leak signature id."),
    };
}

using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// Renders a <see cref="RequestSummary"/> as one MEL <c>Information</c> event
/// with named placeholders so the message text is readable by a human grep
/// and the individual fields are recoverable by structured sinks (the audit
/// JSON file, or a future test that asserts on properties).
/// </summary>
internal sealed class RequestSummaryLogger
{
    private readonly ILogger<RequestSummaryLogger> _log;

    public RequestSummaryLogger(ILogger<RequestSummaryLogger> log)
    {
        _log = log;
    }

    public void Log(RequestSummary s)
    {
        // The per-request trace id is NOT rendered here: the endpoint pushes it
        // onto the log context as "ReqTrace" for the whole handler, and
        // ReqTraceFormatEnricher prefixes EVERY in-request line — this summary
        // included — with "[<id>] ". So the operator greps that id (shared with
        // the enter/exit and pipeline lines) and finds the matching audit-JSON
        // files (<id>-{kind}.json). One id, rendered in one place; the summary
        // message carries none itself (no self-rendered id to double or to
        // collide with the framework's ambient Activity "TraceId" scope).
        //
        // The leading literal "summary" is a stable grep anchor for this line
        // (it's the one per-request line that rolls up model/target/betas/usage);
        // it's a fixed token, not a template hole, so nothing can shadow it.
        //
        // Level reflects the response status so a `grep ERR` pulls just the
        // 5xx, a `grep WRN` adds the 4xx, and tail -f stays readable for
        // 2xx traffic — the same line shape is used at every level.
        _log.Log(
            LevelForStatus(s.StatusCode),
            "summary {Kind} requested={RequestedModel} resolved={ResolvedModel} profile={CanonicalProfileId} "
            + "target={TargetVendor}:{TargetEndpoint} "
            + "betas_in=[{InboundBetasCsv}] betas_out=[{OutboundBetasCsv}] "
            + "effort={EffortDisplay} max_tokens={MaxTokensDisplay} usage={UsageDisplay} "
            + "status={StatusCode} streaming={Streaming} response_leak={ResponseLeakDetected} runaway={RunawayDetected} tool_input_invalid={ToolInputInvalidDetected} "
            + "poisoned_tool_results={PoisonedToolResults} duration_ms={DurationMs} error={ErrorDisplay}",
            s.Kind,
            s.RequestedModel ?? "?",
            s.ResolvedModel ?? "?",
            s.CanonicalProfileId ?? "?",
            s.TargetVendor ?? "?",
            s.TargetEndpoint ?? "?",
            s.InboundBetasCsv,
            s.OutboundBetasCsv,
            s.EffortDisplay,
            s.MaxTokensDisplay,
            s.Usage.Display,
            s.StatusCode,
            s.Streaming,
            s.ResponseLeakDetected,
            s.RunawayDetected,
            s.ToolInputInvalidDetected,
            s.PoisonedToolResults,
            s.DurationMs,
            s.Error ?? "(none)");
    }

    /// <summary>
    /// Map an HTTP status to a log level so failed requests don't hide at
    /// Information. <c>2xx/3xx → Info</c> (normal traffic), <c>4xx → Warn</c>
    /// (client / config issue — bridge's own 400-on-unknown-model and any
    /// upstream rejection both land here), <c>5xx → Error</c> (upstream
    /// outage, internal fault). Status 0 (we never reached upstream) is
    /// treated as Error too.
    /// </summary>
    private static LogLevel LevelForStatus(int status)
    {
        if (status == 0 || status >= 500) return LogLevel.Error;
        if (status >= 400) return LogLevel.Warning;
        return LogLevel.Information;
    }
}

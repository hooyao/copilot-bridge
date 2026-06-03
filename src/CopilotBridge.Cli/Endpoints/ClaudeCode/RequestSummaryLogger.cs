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
        // One template covers both endpoints. {TraceId} sits up front so
        // the operator can grep the line then immediately find the matching
        // audit-JSON files (<TraceId>-{kind}.json) in the trace directory.
        //
        // Level reflects the response status so a `grep ERR` pulls just the
        // 5xx, a `grep WRN` adds the 4xx, and tail -f stays readable for
        // 2xx traffic — the same line shape is used at every level.
        _log.Log(
            LevelForStatus(s.StatusCode),
            "req#{TraceId} {Kind} requested={RequestedModel} resolved={ResolvedModel} profile={CanonicalProfileId} "
            + "target={TargetVendor}:{TargetEndpoint} "
            + "betas_in=[{InboundBetasCsv}] betas_out=[{OutboundBetasCsv}] "
            + "effort={EffortDisplay} max_tokens={MaxTokensDisplay} usage={UsageDisplay} "
            + "status={StatusCode} streaming={Streaming} duration_ms={DurationMs}",
            s.TraceId,
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
            s.DurationMs);
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

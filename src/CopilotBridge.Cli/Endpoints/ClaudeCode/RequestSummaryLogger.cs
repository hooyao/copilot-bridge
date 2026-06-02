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
        // One template covers both endpoints. The placeholder set is kept
        // alphabetical-ish but groups related fields (model trio, target,
        // betas pair, effort, tokens). Structured loggers pick out the
        // named properties; humans read the rendered line.
        _log.LogInformation(
            "req {Kind} requested={RequestedModel} resolved={ResolvedModel} profile={CanonicalProfileId} "
            + "target={TargetVendor}:{TargetEndpoint} "
            + "betas_in=[{InboundBetasCsv}] betas_out=[{OutboundBetasCsv}] "
            + "effort={EffortDisplay} max_tokens={MaxTokensDisplay} usage={UsageDisplay} "
            + "status={StatusCode} streaming={Streaming} duration_ms={DurationMs}",
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
}

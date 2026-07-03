using CopilotBridge.Cli.Models;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// Per-request IR captured by the endpoint and emitted as one INFO line in
/// the <c>finally</c> block. Lets the operator answer "what model did the
/// client ask for, what did we actually send to Copilot, with which betas,
/// at what effort, and how many tokens did the round trip cost?" in a single
/// grep-friendly line.
/// </summary>
/// <remarks>
/// Fields capturing "before vs after pipeline" deltas (<see cref="InboundEffort"/>
/// /<see cref="OutboundEffort"/>) are snapshotted at the right moment: the
/// endpoint records the inbound value off the deserialised client body before
/// the pipeline runs, then reads the outbound value off
/// <c>bridgeCtx.Request.Body</c> after <c>runner.RunAsync</c> completes.
/// </remarks>
internal sealed class RequestSummary
{
    public required string Kind { get; init; }                  // "messages" or "count_tokens"
    public string? RequestedModel { get; set; }
    public string? ResolvedModel { get; set; }
    public string? CanonicalProfileId { get; set; }
    public string? TargetVendor { get; set; }
    public string? TargetEndpoint { get; set; }
    public IReadOnlyList<string> InboundBetas { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> OutboundBetas { get; set; } = Array.Empty<string>();
    public string? InboundEffort { get; set; }
    public string? OutboundEffort { get; set; }
    public int? MaxTokens { get; set; }
    public UsageSnapshot Usage { get; set; } = new();
    public int StatusCode { get; set; }
    public bool Streaming { get; set; }
    public long DurationMs { get; set; }

    /// <summary>
    /// True when the response-leak guard detected a leak — a leaked tool call or a
    /// leaked control envelope — and forced a client retry this turn. Lets the
    /// operator measure the real-world leak rate by grepping the summary line
    /// (<c>response_leak=</c>). Default false.
    /// </summary>
    public bool ResponseLeakDetected { get; set; }

    /// <summary>
    /// When the pipeline / endpoint throws, the exception's type + message
    /// land here so the INFO line surfaces what failed (e.g.
    /// <c>NullReferenceException: Object reference not set</c>) without
    /// requiring the operator to also fish out the stack-trace log line.
    /// Null on success.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// "max" (in==out), "max→xhigh" (in!=out), or "(none)" when no effort
    /// was set on either side.
    /// </summary>
    public string EffortDisplay
    {
        get
        {
            if (InboundEffort is null && OutboundEffort is null) return "(none)";
            if (string.Equals(InboundEffort, OutboundEffort, StringComparison.Ordinal))
            {
                return InboundEffort ?? "(none)";
            }
            return $"{InboundEffort ?? "(none)"}→{OutboundEffort ?? "(none)"}";
        }
    }

    public string InboundBetasCsv => InboundBetas.Count == 0 ? "" : string.Join(",", InboundBetas);
    public string OutboundBetasCsv => OutboundBetas.Count == 0 ? "" : string.Join(",", OutboundBetas);
    public string MaxTokensDisplay => MaxTokens?.ToString() ?? "(unset)";
}

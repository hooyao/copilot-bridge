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
    /// <summary>
    /// Stable per-request id shared with the four audit JSON files
    /// (<c>{TraceId}-{kind}.json</c>) — built once at the inbound endpoint
    /// as <c>{yyyyMMdd-HHmmss}-{seq:D4}</c>. The INFO summary line renders
    /// this as <c>req#&lt;TraceId&gt;</c>; the operator greps the log for
    /// the id and immediately knows which trace files to open.
    /// </summary>
    public required string TraceId { get; init; }
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

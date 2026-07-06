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
    /// True when the runaway guard (<c>RunawayGuardDetector</c>) aborted the turn
    /// because the streamed response exceeded its byte / delta-count budget (the
    /// degenerate-generation signature). Surfaced on the summary line as
    /// <c>runaway=</c> so trips are grep-able and the rate is measurable. Distinct
    /// from <see cref="ResponseLeakDetected"/> (protocol leak, not volume). Default
    /// false.
    /// </summary>
    public bool RunawayDetected { get; set; }

    /// <summary>
    /// True when tool input validation aborted the response because a real
    /// <c>tool_use</c> block produced malformed JSON or violated the declared tool
    /// schema. Emitted as <c>tool_input_invalid=</c>. Default false.
    /// </summary>
    public bool ToolInputInvalidDetected { get; set; }

    /// <summary>
    /// Count of inbound <c>tool_result</c> blocks carrying a replayed API-error
    /// payload (content starting with <c>"API Error:"</c>) — failure debris from
    /// earlier failed tool / sub-agent calls in the same session that Claude Code
    /// keeps in the transcript and resends. A heavily poisoned context can derail a
    /// weak backend model (it triggered the gpt-5.5 runaway). Produced by
    /// <c>PoisonedContextScanStage</c> in the request pipeline and copied here by the
    /// endpoint; that stage also logs a "compact your session" WARNING once the count
    /// crosses its threshold. COUNT ONLY — the transcript is never mutated (dropping a
    /// <c>tool_result</c> without its paired <c>tool_use</c> would 400 upstream, and
    /// the bridge cannot un-poison the client's history — only compacting can).
    /// Emitted on the summary line as <c>poisoned_tool_results=</c>. Default 0.
    /// </summary>
    public int PoisonedToolResults { get; set; }

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

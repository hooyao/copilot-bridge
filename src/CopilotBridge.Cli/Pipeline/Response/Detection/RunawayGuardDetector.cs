using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Volume circuit-breaker on the streamed IR Anthropic response. Aborts a
/// degenerate-generation runaway — a model stuck emitting an unbounded stream of
/// tiny <c>content_block_delta</c> fragments — before it hangs the client for
/// minutes. Trips on either budget in <see cref="RunawayGuardOptions"/>:
/// cumulative delta bytes (<see cref="RunawayGuardOptions.MaxDeltaBytes"/>) across
/// the whole response, or per-content-block delta count
/// (<see cref="RunawayGuardOptions.MaxDeltaCount"/>).
/// </summary>
/// <remarks>
/// <para>
/// Scoped DI service (per-request counters must not leak across requests). It runs
/// inside <see cref="ResponseInspectionStage"/>, which wraps BOTH the Codex T3
/// output (gpt-5.5 etc.) and the <c>/cc</c> Anthropic passthrough, so a runaway on
/// either path is caught. Measures <c>evt.Data</c> length directly (a parse-free
/// proxy for output volume) — post-T3 the events are already clean Anthropic
/// deltas, so this counts the real payload the client would receive.
/// </para>
/// <para>
/// On a trip it reuses <see cref="ResponseLeakError"/>'s wire shape
/// (<c>overloaded_error</c> by default) so Claude Code treats it as retryable, and
/// sets <see cref="BridgeContext{TBody}.RunawayDetected"/> so the endpoint can
/// surface <c>runaway=true</c> on the summary line — distinct from a protocol leak.
/// </para>
/// </remarks>
internal sealed class RunawayGuardDetector : AbstractOrderAwareDetector<RunawayGuardDetector>
{
    private readonly RunawayGuardOptions _opts;
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger _log;

    // Per-request counters, reset in Begin().
    private long _totalDeltaBytes;
    private int _blockDeltaCount;

    public RunawayGuardDetector(
        DetectorOrder<RunawayGuardDetector> order,
        IOptionsSnapshot<RunawayGuardOptions> opts,
        BridgeContext<MessagesRequest> ctx,
        ILogger<RunawayGuardDetector> log) : base(order)
    {
        _opts = opts.Value;
        _ctx = ctx;
        _log = log;
    }

    public override string Name => "RunawayGuard";

    public override bool Enabled => _opts.Enabled;

    public override void Begin()
    {
        _totalDeltaBytes = 0;
        _blockDeltaCount = 0;
    }

    public override DetectionAction InspectEvent(in SseItem<string> evt)
    {
        switch (evt.EventType)
        {
            case "content_block_start":
                // A new block resets the per-block delta count (the degenerate
                // signature is one block emitting tens of thousands of fragments);
                // the cumulative byte budget spans the whole response and is NOT
                // reset here.
                _blockDeltaCount = 0;
                break;

            case "content_block_delta":
                _blockDeltaCount++;
                // evt.Data is the whole SSE data payload for this delta; its length
                // is a parse-free proxy for the streamed volume. Count it against
                // the cumulative byte budget.
                _totalDeltaBytes += evt.Data.Length;

                if (_opts.MaxDeltaCount > 0 && _blockDeltaCount > _opts.MaxDeltaCount)
                    return Trip($"delta count {_blockDeltaCount} exceeded MaxDeltaCount {_opts.MaxDeltaCount} in a single content block");

                if (_totalDeltaBytes > _opts.MaxDeltaBytes)
                    return Trip($"cumulative delta bytes {_totalDeltaBytes} exceeded MaxDeltaBytes {_opts.MaxDeltaBytes}");
                break;
        }

        return DetectionAction.None;
    }

    /// <summary>Log the trip, mark the context flag, and build the abort action —
    /// the same retryable <c>overloaded_error</c> envelope the leak guard uses.</summary>
    private DetectionAction Trip(string reason)
    {
        var signal = _opts.Signal;
        _log.LogWarning(
            "runaway detected: {Reason}; signal={Signal} — aborting the turn with a retryable error "
            + "(tune Pipeline:Detectors:RunawayGuard, restart required after changing a value)",
            reason,
            ResponseLeakError.ErrorType(signal));
        _ctx.RunawayDetected = true;
        return DetectionAction.Abort(
            ResponseLeakError.JsonWithMessage(signal, RunawayMessage),
            ResponseLeakError.HttpStatus(signal));
    }

    /// <summary>Client-facing abort message. No <c>"</c> or <c>\</c> (embedded in
    /// hand-built JSON without escaping — same constraint as
    /// <see cref="ResponseLeakError.Message"/>).</summary>
    private const string RunawayMessage =
        "[copilot-bridge] The upstream model produced a runaway response "
        + "(exceeded the configured size/length budget) and was aborted; forcing a clean retry. "
        + "If this is a false positive on a legitimately large output, raise "
        + "Pipeline:Detectors:RunawayGuard:MaxDeltaBytes / MaxDeltaCount in appsettings.json and restart copilot-bridge.";
}

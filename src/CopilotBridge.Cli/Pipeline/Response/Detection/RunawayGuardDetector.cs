using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Volume/degeneracy circuit-breaker on the streamed IR Anthropic response. Aborts a
/// degenerate-generation runaway — a model stuck emitting an unbounded stream of
/// tiny <c>content_block_delta</c> fragments, or one repeating the same token — before
/// it hangs the client for minutes. Trips on any of three signals in
/// <see cref="RunawayGuardOptions"/>: cumulative delta bytes
/// (<see cref="RunawayGuardOptions.MaxDeltaBytes"/>) across the whole response,
/// per-content-block delta count (<see cref="RunawayGuardOptions.MaxDeltaCount"/>), or
/// per-content-block token-repetition density
/// (<see cref="RunawayGuardOptions.RepetitionWindow"/> /
/// <see cref="RunawayGuardOptions.RepetitionMinUniqueRatio"/>).
/// </summary>
/// <remarks>
/// <para>
/// Scoped DI service (per-request counters must not leak across requests). It runs
/// inside <see cref="ResponseInspectionStage"/>, which wraps BOTH the Codex T3
/// output (gpt-5.5 etc.) and the <c>/cc</c> Anthropic passthrough, so a runaway on
/// either path is caught. The volume signals measure <c>evt.Data</c> length directly
/// (a parse-free proxy for output volume); the repetition signal parses only the
/// visible <c>text_delta</c>/<c>thinking_delta</c> text and maintains a bounded
/// trailing-token window, so it catches a single-token loop that stays under both
/// volume budgets (observed: <c>claude-opus-4.8</c> repeating one token ~32,000× to
/// <c>max_tokens</c> in ~1,010 deltas / ~500 KB).
/// </para>
/// <para>
/// On a trip it reuses <see cref="ResponseDetectionError"/>'s wire shape
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

    // Repetition signal, per content block. A ring buffer of the trailing
    // RepetitionWindow tokens plus a multiset of their counts gives an O(1)-amortised
    // unique-token ratio without rescanning. _tokenTail carries a partial token across
    // delta boundaries (a token is only counted once its terminating whitespace lands).
    private string[]? _ring;
    private int _ringHead;      // next write position (circular)
    private int _ringCount;     // tokens currently in the ring (<= window)
    private Dictionary<string, int>? _ringCounts;
    private string _tokenTail = "";

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
        ResetRepetition();
    }

    private void ResetRepetition()
    {
        var window = _opts.RepetitionWindow;
        if (window > 0)
        {
            // Allocate lazily to window size; reused per block by clearing counts.
            if (_ring is null || _ring.Length != window)
            {
                _ring = new string[window];
                _ringCounts = new Dictionary<string, int>(window, StringComparer.Ordinal);
            }
            else
            {
                _ringCounts!.Clear();
            }
        }
        _ringHead = 0;
        _ringCount = 0;
        _tokenTail = "";
    }

    public override DetectionAction InspectEvent(in SseItem<string> evt)
    {
        switch (evt.EventType)
        {
            case "content_block_start":
                // A new block resets the per-block delta count (the degenerate
                // signature is one block emitting tens of thousands of fragments);
                // the cumulative byte budget spans the whole response and is NOT
                // reset here. The repetition window is also per-block.
                _blockDeltaCount = 0;
                ResetRepetition();
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

                if (_opts.RepetitionWindow > 0 && FeedRepetition(evt.Data, out var repReason))
                    return Trip(repReason);
                break;
        }

        return DetectionAction.None;
    }

    /// <summary>
    /// Feed a <c>content_block_delta</c>'s visible text into the trailing-token window
    /// and report whether the block has degenerated into low-diversity repetition. A
    /// non-text delta (e.g. <c>input_json_delta</c>) contributes nothing. Returns true
    /// with a reason only when the window is FULL and its unique-token ratio is below
    /// the configured floor.
    /// </summary>
    private bool FeedRepetition(string deltaData, out string reason)
    {
        reason = "";
        var text = ExtractDeltaText(deltaData);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Prepend the carried partial token; split on whitespace. The final piece is a
        // partial token unless the text ended on whitespace — carry it to the next delta.
        var combined = _tokenTail + text;
        var endsOnBoundary = char.IsWhiteSpace(combined[^1]);
        var pieces = combined.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var complete = endsOnBoundary ? pieces.Length : pieces.Length - 1;
        _tokenTail = endsOnBoundary ? "" : (pieces.Length > 0 ? pieces[^1] : combined);

        for (var i = 0; i < complete; i++)
        {
            PushToken(pieces[i]);

            // Only meaningful once the window is full: a short legitimate repetition
            // (fewer than window tokens) must not trip.
            if (_ringCount >= _opts.RepetitionWindow)
            {
                var distinct = _ringCounts!.Count;
                if (distinct < _opts.RepetitionWindow * _opts.RepetitionMinUniqueRatio)
                {
                    reason = $"repetition: {distinct} unique of the trailing {_opts.RepetitionWindow} tokens "
                        + $"(ratio {(double)distinct / _opts.RepetitionWindow:0.###} < RepetitionMinUniqueRatio {_opts.RepetitionMinUniqueRatio}) in a single content block";
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Push one token into the ring, evicting the oldest when full, keeping
    /// the multiset of counts in sync so <c>_ringCounts.Count</c> is the distinct count.</summary>
    private void PushToken(string token)
    {
        var ring = _ring!;
        var counts = _ringCounts!;
        if (_ringCount >= ring.Length)
        {
            // Evict the token at the head (the oldest) before overwriting it.
            var old = ring[_ringHead];
            if (--counts[old] == 0)
            {
                counts.Remove(old);
            }
        }
        else
        {
            _ringCount++;
        }
        ring[_ringHead] = token;
        counts[token] = counts.TryGetValue(token, out var c) ? c + 1 : 1;
        _ringHead = (_ringHead + 1) % ring.Length;
    }

    /// <summary>Extract the streamed visible text off a <c>content_block_delta</c>:
    /// <c>text_delta.text</c> or <c>thinking_delta.thinking</c>. Mirrors
    /// <see cref="ResponseLeakDetector"/>'s extraction; parse failures yield null.</summary>
    private static string? ExtractDeltaText(string deltaData)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(deltaData);
            if (!doc.RootElement.TryGetProperty("delta", out var delta)
                || !delta.TryGetProperty("type", out var dt))
            {
                return null;
            }
            return dt.GetString() switch
            {
                "text_delta" => delta.TryGetProperty("text", out var x) ? x.GetString() : null,
                "thinking_delta" => delta.TryGetProperty("thinking", out var x) ? x.GetString() : null,
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
            ResponseDetectionError.ErrorType(signal));
        _ctx.RunawayDetected = true;
        return DetectionAction.Abort(
            ResponseDetectionError.JsonWithMessage(signal, RunawayMessage),
            ResponseDetectionError.HttpStatus(signal));
    }

    /// <summary>Client-facing abort message. No <c>"</c> or <c>\</c> (embedded in
    /// hand-built JSON without escaping — same constraint as
    /// <see cref="ResponseDetectionError.Message"/>).</summary>
    private const string RunawayMessage =
        "[copilot-bridge] The upstream model produced a runaway response "
        + "(exceeded the configured size/length budget) and was aborted; forcing a clean retry. "
        + "If this is a false positive on a legitimately large output, raise "
        + "Pipeline:Detectors:RunawayGuard:MaxDeltaBytes / MaxDeltaCount in appsettings.json and restart copilot-bridge.";
}

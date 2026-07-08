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
    // Whole-request gate for the repetition signal: window > 0 AND the ratio floor is a
    // usable fraction (0 < ratio < 1). An out-of-range ratio (a config typo like 0, a
    // negative, or >= 1 which would force a trip on any full window) disables the signal
    // rather than force-aborting every response. Computed once in Begin().
    private bool _repetitionEnabled;
    // The effective (clamped) window used for the fullness gate, ratio floor, and tail
    // cap. NOT _opts.RepetitionWindow directly — that can be an absurd config value; the
    // ring is sized to this, so every comparison must use this too.
    private int _window;
    // Cap on the carried partial token so a whitespace-free run (base64, minified JSON,
    // CJK) cannot make `_tokenTail + text` reallocate ever-larger strings (quadratic).
    // A token longer than the window is itself degenerate and its exact bytes past the
    // cap do not change the unique-token ratio, so truncating is safe. Bounded per delta.
    private int _tokenTailCap;

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

    // Upper bound on RepetitionWindow. The detector is a per-request scoped service, so
    // _ring is allocated per request — a fat-fingered config (an extra few zeros) must
    // not OOM the bridge on the first /cc request. 100k tokens is orders of magnitude
    // above any legitimate window (the signal targets ~500) and caps the array at ~800 KB.
    private const int MaxRepetitionWindow = 100_000;

    private void ResetRepetition()
    {
        var ratio = _opts.RepetitionMinUniqueRatio;
        // Clamp the window to a sane ceiling so a config typo can't allocate a huge
        // per-request array (see MaxRepetitionWindow).
        var window = Math.Min(_opts.RepetitionWindow, MaxRepetitionWindow);
        // Enable only for a positive window AND a usable ratio floor (0 < ratio < 1).
        // ratio <= 0 can never trip (dead config); ratio >= 1 would trip on EVERY full
        // window (distinct <= window always) — a config typo must not force-abort every
        // response, so treat out-of-range as disabled.
        _repetitionEnabled = window > 0 && ratio > 0.0 && ratio < 1.0;
        if (_repetitionEnabled)
        {
            _window = window;
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
            // A partial token never needs to exceed the window to affect the ratio; cap
            // it there so a whitespace-free run cannot grow the tail unboundedly. window
            // is >= 1 here (guarded by _repetitionEnabled).
            _tokenTailCap = window;
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

                if (_repetitionEnabled && FeedRepetition(evt.Data, out var repReason))
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

        // Prepend any carried partial token so a token split across deltas is treated as
        // one token. String.Concat returns `text` unchanged when _tokenTail is empty (the
        // common case — most deltas end on whitespace), so this allocates only when a
        // token actually spans a delta boundary.
        var combined = _tokenTail + text;
        var span = combined.AsSpan();
        var n = span.Length;
        var i = 0;

        // Default: this delta leaves no trailing partial token. Overwritten below if it
        // ends mid-token.
        _tokenTail = string.Empty;

        // Span-keyed lookup into the multiset: probe/increment counts WITHOUT allocating a
        // substring per token. A substring is materialized only for a token that is new to
        // the window (see PushToken). For a degenerate single-token loop this collapses
        // ~one allocation per repeated fragment down to ~one for the whole response.
        var lookup = _ringCounts!.GetAlternateLookup<ReadOnlySpan<char>>();

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(span[i])) i++;
            if (i >= n) break;
            var start = i;
            while (i < n && !char.IsWhiteSpace(span[i])) i++;

            if (i == n)
            {
                // Reached the end with no terminating whitespace → this token is
                // incomplete; carry it to the next delta, bounded so a whitespace-free
                // run (base64/minified/CJK) cannot grow it unboundedly. Truncating a token
                // past the window length cannot change the unique-token ratio.
                var tail = span[start..];
                if (tail.Length > _tokenTailCap) tail = tail[.._tokenTailCap];
                _tokenTail = tail.ToString();
                break;
            }

            PushToken(span[start..i], lookup);

            // Only meaningful once the window is full: a short legitimate repetition
            // (fewer than window tokens) must not trip. Uses the clamped _window, which
            // the ring is sized to (NOT the raw config value).
            if (_ringCount >= _window)
            {
                var distinct = _ringCounts.Count;
                if (distinct < _window * _opts.RepetitionMinUniqueRatio)
                {
                    reason = $"repetition: {distinct} unique of the trailing {_window} tokens "
                        + $"(ratio {(double)distinct / _window:0.###} < RepetitionMinUniqueRatio {_opts.RepetitionMinUniqueRatio}) in a single content block";
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Push one token (as a span) into the ring, evicting the oldest when full,
    /// keeping the multiset of counts in sync so <c>_ringCounts.Count</c> is the distinct
    /// count. Reuses the stored key string when the token is already in the window (no
    /// allocation); materializes the token once only when it is new.</summary>
    private void PushToken(ReadOnlySpan<char> token, Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup)
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

        string tokenStr;
        if (lookup.TryGetValue(token, out var existingKey, out var c))
        {
            // Already in the window: reuse the stored string, no allocation.
            tokenStr = existingKey;
            counts[existingKey] = c + 1;
        }
        else
        {
            // New to the window: materialize the key once.
            tokenStr = token.ToString();
            counts[tokenStr] = 1;
        }
        ring[_ringHead] = tokenStr;
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
        + "(exceeded a configured volume budget or degenerated into repeated tokens) and was aborted; forcing a clean retry. "
        + "If this is a false positive on a legitimately large or repetitive output, raise "
        + "Pipeline:Detectors:RunawayGuard:MaxDeltaBytes / MaxDeltaCount / RepetitionMinUniqueRatio (or lower RepetitionWindow to 0) in appsettings.json and restart copilot-bridge.";
}

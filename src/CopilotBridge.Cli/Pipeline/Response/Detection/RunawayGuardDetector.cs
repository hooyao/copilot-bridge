using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Volume/degeneracy circuit-breaker on the Anthropic response. Aborts a
/// degenerate-generation runaway — a model stuck emitting an unbounded stream of
/// tiny <c>content_block_delta</c> fragments, or one repeating the same token — before
/// it hangs the client for minutes. Trips on any of four signals in
/// <see cref="RunawayGuardOptions"/>: cumulative delta bytes
/// (<see cref="RunawayGuardOptions.MaxDeltaBytes"/>) across the whole response,
/// per-content-block delta count (<see cref="RunawayGuardOptions.MaxDeltaCount"/>),
/// per-content-block token-repetition density
/// (<see cref="RunawayGuardOptions.RepetitionWindow"/> /
/// <see cref="RunawayGuardOptions.RepetitionMinUniqueRatio"/>), or per-content-block
/// consecutive-run length (<see cref="RunawayGuardOptions.RepetitionMaxConsecutiveRepeat"/>).
/// </summary>
/// <remarks>
/// <para>
/// Scoped DI service (per-request counters must not leak across requests). It runs
/// inside <see cref="ResponseInspectionStage"/>, which wraps BOTH the Codex T3
/// output (gpt-5.5 etc.) and the <c>/cc</c> Anthropic passthrough, so a runaway on
/// either path is caught. Both delivery paths are covered: streaming via
/// <see cref="InspectEvent"/> (the two volume budgets measure <c>evt.Data</c> length
/// directly, a parse-free proxy for output volume; the content-keyed signals parse
/// only the visible <c>text_delta</c>/<c>thinking_delta</c> text), and a buffered
/// (<c>application/json</c>) body — which Copilot returns when it ignores
/// <c>stream:true</c> — via <see cref="InspectBuffered"/>, which feeds each
/// <c>text</c>/<c>thinking</c> block through the same per-block core (the volume
/// budgets do not apply to a body with no deltas). The density signal catches a long
/// single-token loop that stays under both volume budgets (observed:
/// <c>claude-opus-4.8</c> repeating one token ~32,000× to <c>max_tokens</c> in ~1,010
/// deltas / ~500 KB); the run-length signal catches a SHORT flood the window can never
/// fill (observed: ~100 repeats in a 108-token buffered body).
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
    // Fallback tail cap when the repetition-density window is disabled but the run-length
    // signal is on (so there is no _window to size the cap from). A single token longer
    // than this is itself degenerate; its exact bytes past the cap change neither the run
    // count nor memory safety, and this only bounds the carried tail against a
    // whitespace-free run.
    private const int RunLengthTailCap = 1024;

    // Run-length signal, per content block. Trips when the SAME token is emitted
    // _maxConsecutive times in a row — a short single-token flood that never fills the
    // sliding window (so the density ratio can't evaluate) and stays under both volume
    // budgets. _consecutiveRun counts identical repeats; the previous complete token is
    // kept in a REUSED buffer (not a per-token string) so equality is alloc-free in
    // steady state and does NOT depend on the density ring — the signal works when the
    // window signal is disabled and on the buffered path. Reset per block.
    private bool _runLengthEnabled;
    private int _maxConsecutive;
    private int _consecutiveRun;
    // Previous complete token, in a reusable buffer that grows on demand. The STORED
    // token length is capped at _tokenTailCap (see FeedRunLength), so a whitespace-free
    // run cannot grow it without bound; the backing array's CAPACITY may reach up to
    // ~2×_tokenTailCap due to geometric doubling, but never beyond. _prevTokenLen < 0
    // means "no previous token yet".
    private char[] _prevTokenBuf = new char[64];
    private int _prevTokenLen = -1;

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
        else
        {
            // Density signal off. If the run-length signal still tokenizes, the carried
            // tail needs a bound too (there is no window to size it from).
            _tokenTailCap = RunLengthTailCap;
        }

        // Run-length signal: independent of the window. A threshold <= 0 disables it.
        _maxConsecutive = _opts.RepetitionMaxConsecutiveRepeat;
        _runLengthEnabled = _maxConsecutive > 0;
        _consecutiveRun = 0;
        _prevTokenLen = -1;

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
                    return Trip($"delta count {_blockDeltaCount} exceeded MaxDeltaCount {_opts.MaxDeltaCount} in a single content block", "stream");

                if (_totalDeltaBytes > _opts.MaxDeltaBytes)
                    return Trip($"cumulative delta bytes {_totalDeltaBytes} exceeded MaxDeltaBytes {_opts.MaxDeltaBytes}", "stream");

                if ((_repetitionEnabled || _runLengthEnabled)
                    && FeedRepetition(ExtractDeltaText(evt.Data), out var repReason))
                    return Trip(repReason, "stream");
                break;
        }

        return DetectionAction.None;
    }

    /// <summary>
    /// Buffered (non-streaming) counterpart of <see cref="InspectEvent"/>: scan a whole
    /// Anthropic Messages response body once. Copilot sometimes ignores a request's
    /// <c>stream:true</c> and returns a one-shot <c>application/json</c> body — on that
    /// path there are no <c>content_block_delta</c> events, so the streaming-only volume
    /// budgets (<see cref="RunawayGuardOptions.MaxDeltaBytes"/> /
    /// <see cref="RunawayGuardOptions.MaxDeltaCount"/>) do not apply; the content-keyed
    /// repetition-density and run-length signals do. Feeds each <c>text</c> block's
    /// <c>text</c> and each <c>thinking</c> block's <c>thinking</c> through the same
    /// per-block core as streaming (each block a fresh scope, mirroring the streaming
    /// per-<c>content_block_start</c> reset). Aborts on the first block that degenerates,
    /// with the same <see cref="Trip"/> error + <c>runaway=true</c> flag as streaming.
    /// Fails open (returns None) on a body that isn't parseable Anthropic JSON, so a parse
    /// hiccup never turns a real response into an error.
    /// </summary>
    public override DetectionAction InspectBuffered(byte[] body)
    {
        // Nothing to scan when both content signals are off (the volume budgets are
        // streaming-only). Cheap early-out; also avoids a needless parse.
        if (!_repetitionEnabled && !_runLengthEnabled)
        {
            return DetectionAction.None;
        }

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            // Fail open: a body this safety guard can't parse is delivered as-is (a real
            // runaway always lives inside a well-formed envelope's text, so this cannot
            // swallow one). Log at debug so an upstream-shape regression is diagnosable
            // without touching the happy path.
            _log.LogDebug("runaway guard: buffered body is not parseable JSON ({Bytes} bytes) — failing open", body.Length);
            return DetectionAction.None;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return DetectionAction.None;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !block.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    // A block with no string `type` is not a scannable content block;
                    // skip it. The ValueKind gate keeps GetString() from throwing
                    // InvalidOperationException on a non-string `type` (which would
                    // escape the JsonException-scoped fail-open above and turn a real
                    // response into a 502) — fail open on a malformed block, like a
                    // malformed body.
                    continue;
                }
                // Mirror the streaming path, which feeds both text_delta and
                // thinking_delta (RunawayGuard has no ScanThinking gate — that is a
                // leak-guard concept). A non-scannable block (tool_use, …) contributes
                // nothing, exactly as its non-text deltas contribute nothing when streamed.
                var field = typeEl.GetString() switch
                {
                    "text" => "text",
                    "thinking" => "thinking",
                    _ => null,
                };
                if (field is null
                    || !block.TryGetProperty(field, out var textEl)
                    || textEl.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                // Fresh per-block scope, the same reset streaming does at
                // content_block_start, so an earlier block cannot poison a later one.
                ResetRepetition();
                if (FeedRepetition(textEl.GetString(), out var reason))
                {
                    return Trip(reason, "buffer");
                }
            }
        }

        return DetectionAction.None;
    }

    /// <summary>
    /// Tokenize <paramref name="text"/> and report whether the block has degenerated.
    /// Shared by the streaming path (one
    /// <c>text_delta</c>/<c>thinking_delta</c>'s text) and the buffered path (a whole
    /// block's text). Runs two independent signals over the same whitespace-tokenized
    /// stream, both scoped to the current block:
    /// <list type="bullet">
    /// <item><b>density</b> — the trailing-<c>_window</c> unique-token ratio (only when
    /// <c>_repetitionEnabled</c>);</item>
    /// <item><b>run-length</b> — the count of consecutive identical tokens (only when
    /// <c>_runLengthEnabled</c>), which catches a short flood the window can't fill.</item>
    /// </list>
    /// <paramref name="text"/> is the already-extracted visible text (null/empty for a
    /// non-text delta such as <c>input_json_delta</c>) — extraction is the caller's job so
    /// the buffered path can pass a block string directly. Returns true with a reason on
    /// the first signal to trip.
    /// </summary>
    private bool FeedRepetition(string? text, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Prepend any carried partial token so a token split across chunks is treated as
        // one token. String.Concat returns `text` unchanged when _tokenTail is empty (the
        // common case — most chunks end on whitespace), so this allocates only when a
        // token actually spans a chunk boundary.
        var combined = _tokenTail + text;
        var span = combined.AsSpan();
        var n = span.Length;
        var i = 0;

        // Default: this chunk leaves no trailing partial token. Overwritten below if it
        // ends mid-token.
        _tokenTail = string.Empty;

        // Span-keyed lookup into the multiset: probe/increment counts WITHOUT allocating a
        // substring per token. A substring is materialized only for a token that is new to
        // the window (see PushToken). For a degenerate single-token loop this collapses
        // ~one allocation per repeated fragment down to ~one for the whole response. Only
        // valid when the density ring exists (_repetitionEnabled).
        var lookup = _repetitionEnabled
            ? _ringCounts!.GetAlternateLookup<ReadOnlySpan<char>>()
            : default;

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(span[i])) i++;
            if (i >= n) break;
            var start = i;
            while (i < n && !char.IsWhiteSpace(span[i])) i++;

            if (i == n)
            {
                // Reached the end with no terminating whitespace → this token is
                // incomplete; carry it to the next chunk, bounded so a whitespace-free
                // run (base64/minified/CJK) cannot grow it unboundedly. Truncating a token
                // past the cap changes neither the unique-token ratio nor the run count.
                var tail = span[start..];
                if (tail.Length > _tokenTailCap) tail = tail[.._tokenTailCap];
                _tokenTail = tail.ToString();
                break;
            }

            var token = span[start..i];

            // Run-length signal: count consecutive identical tokens. Evaluated before the
            // density push so it does not depend on the ring existing.
            if (_runLengthEnabled && FeedRunLength(token))
            {
                reason = $"repetition: the same token repeated {_consecutiveRun} times in a row "
                    + $"(>= RepetitionMaxConsecutiveRepeat {_maxConsecutive}) in a single content block";
                return true;
            }

            if (!_repetitionEnabled)
            {
                continue;
            }

            PushToken(token, lookup);

            // Only meaningful once the window is full: a short legitimate repetition
            // (fewer than window tokens) must not trip. Uses the clamped _window, which
            // the ring is sized to (NOT the raw config value).
            if (_ringCount >= _window)
            {
                var distinct = _ringCounts!.Count;
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

    /// <summary>Advance the consecutive-run counter with one complete token and report
    /// whether the run has reached <c>_maxConsecutive</c>. Compares against the previous
    /// token held in a reused buffer (alloc-free in steady state); an identical token
    /// increments the run, a different one restarts it at 1 and copies the new token into
    /// the buffer. The <b>stored</b> token is capped at <c>_tokenTailCap</c> (the backing
    /// array's capacity may reach ~2× that via geometric growth, never beyond), so a
    /// whitespace-free megatoken cannot grow it without bound — and its exact bytes past
    /// the cap cannot change a consecutive-equality decision meaningfully.
    /// </summary>
    private bool FeedRunLength(ReadOnlySpan<char> token)
    {
        // Bound the compared/stored token so a single whitespace-free megatoken cannot
        // grow the buffer without limit; its exact bytes past the cap cannot change a
        // consecutive-equality decision meaningfully.
        if (token.Length > _tokenTailCap)
        {
            token = token[.._tokenTailCap];
        }

        var same = _prevTokenLen == token.Length
            && token.SequenceEqual(_prevTokenBuf.AsSpan(0, _prevTokenLen < 0 ? 0 : _prevTokenLen));

        if (same)
        {
            _consecutiveRun++;
            if (_consecutiveRun >= _maxConsecutive)
            {
                return true;
            }
            return false;
        }

        // New token: restart the run and remember it.
        _consecutiveRun = 1;
        if (_prevTokenBuf.Length < token.Length)
        {
            _prevTokenBuf = new char[Math.Max(token.Length, _prevTokenBuf.Length * 2)];
        }
        token.CopyTo(_prevTokenBuf);
        _prevTokenLen = token.Length;
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
    /// the same retryable <c>overloaded_error</c> envelope the leak guard uses.
    /// <paramref name="delivery"/> distinguishes <c>stream</c> (mid-stream injected
    /// error) from <c>buffer</c> (real HTTP status on a one-shot body), mirroring the
    /// leak guard's log so a buffered-path trip is distinguishable. The reason carries
    /// only counts/thresholds — never the runaway content itself.</summary>
    private DetectionAction Trip(string reason, string delivery)
    {
        var signal = _opts.Signal;
        _log.LogWarning(
            "runaway detected: {Reason}; signal={Signal} delivery={Delivery} — aborting the turn with a retryable error "
            + "(tune Pipeline:Detectors:RunawayGuard, restart required after changing a value)",
            reason,
            ResponseDetectionError.ErrorType(signal),
            delivery);
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
        + "Pipeline:Detectors:RunawayGuard:MaxDeltaBytes / MaxDeltaCount / RepetitionMinUniqueRatio / RepetitionMaxConsecutiveRepeat "
        + "(or lower RepetitionWindow to 0) in appsettings.json and restart copilot-bridge.";
}

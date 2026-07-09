using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for <see cref="RunawayGuardDetector"/> — the volume
/// circuit-breaker (#4, <c>docs/gpt55-runaway-diagnosis.md</c>). Contract:
/// <list type="bullet">
///   <item>trips (Abort, retryable <c>overloaded_error</c>) when a single content
///         block emits more than <see cref="RunawayGuardOptions.MaxDeltaCount"/>
///         delta events;</item>
///   <item>trips when cumulative delta bytes exceed
///         <see cref="RunawayGuardOptions.MaxDeltaBytes"/> across the whole
///         response;</item>
///   <item>the per-block count resets at each <c>content_block_start</c>, so two
///         moderate blocks don't sum into a false trip;</item>
///   <item>a normal short stream passes (all None), flag stays clear;</item>
///   <item>the signal selects the error type / HTTP status.</item>
/// </list>
/// Driven directly (not through the full stage) so each budget is exercised in
/// isolation; the stage-level Abort rendering is already covered by
/// <see cref="ResponseInspectionStageTests"/>.
/// </summary>
public class RunawayGuardDetectorTests
{
    private static BridgeContext<MessagesRequest> Ctx() => new()
    {
        Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/cc/v1/messages",
            Body = new MessagesRequest { Model = "gpt-5.5", Messages = Array.Empty<MessageParam>() },
        },
        Response = new BridgeResponse(),
    };

    private static RunawayGuardDetector Detector(BridgeContext<MessagesRequest> ctx, RunawayGuardOptions opts)
    {
        var d = new RunawayGuardDetector(
            new DetectorOrder<RunawayGuardDetector>(3),
            TestOptions.Snapshot(opts),
            ctx,
            NullLogger<RunawayGuardDetector>.Instance);
        d.Begin();
        return d;
    }

    private static SseItem<string> BlockStart(int index) =>
        new($"{{\"type\":\"content_block_start\",\"index\":{index}}}", "content_block_start");

    private static SseItem<string> Delta(string payload) =>
        new(payload, "content_block_delta");

    /// <summary>Feed one block-start then <paramref name="deltas"/> deltas of
    /// <paramref name="payload"/>; return the first non-None action, or None.</summary>
    private static DetectionAction FeedBlock(RunawayGuardDetector d, int deltas, string payload)
    {
        var a = d.InspectEvent(BlockStart(0));
        if (a.Kind != DetectionActionKind.None) return a;
        for (var i = 0; i < deltas; i++)
        {
            a = d.InspectEvent(Delta(payload));
            if (a.Kind != DetectionActionKind.None) return a;
        }
        return DetectionAction.None;
    }

    // ── Delta-count budget ───────────────────────────────────────────────────────

    [Fact]
    public void Trips_WhenBlockDeltaCount_ExceedsBudget()
    {
        var ctx = Ctx();
        // Small byte payload so ONLY the count budget can trip — isolates the check.
        var d = Detector(ctx, new RunawayGuardOptions { MaxDeltaCount = 10, MaxDeltaBytes = long.MaxValue });

        var action = FeedBlock(d, deltas: 11, payload: "x");

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
        Assert.Contains("overloaded_error", action.ErrorJson!);
        Assert.Equal(529, action.HttpStatus);
    }

    [Fact]
    public void DoesNotTrip_AtOrBelowCountBudget()
    {
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions { MaxDeltaCount = 10, MaxDeltaBytes = long.MaxValue });

        // Exactly at the budget (10) must NOT trip (contract: trip when > budget).
        var action = FeedBlock(d, deltas: 10, payload: "x");

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Cumulative-bytes budget ──────────────────────────────────────────────────

    [Fact]
    public void Trips_WhenCumulativeBytes_ExceedBudget()
    {
        var ctx = Ctx();
        // High count budget so only the byte budget can trip. 5 deltas × 100 bytes =
        // 500 > 400 → trips on the 5th (count 5, well under the count budget).
        var d = Detector(ctx, new RunawayGuardOptions { MaxDeltaCount = 1_000_000, MaxDeltaBytes = 400 });

        var action = FeedBlock(d, deltas: 5, payload: new string('y', 100));

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void ByteBudget_AccumulatesAcrossBlocks_NotResetAtBlockStart()
    {
        var ctx = Ctx();
        // Byte budget spans the WHOLE response (unlike the per-block count). Two
        // blocks of 3×100 bytes = 600 > 500 → the cumulative byte budget trips in
        // the second block even though neither block alone exceeds it.
        var d = Detector(ctx, new RunawayGuardOptions { MaxDeltaCount = 1_000_000, MaxDeltaBytes = 500 });

        var first = FeedBlock(d, deltas: 3, payload: new string('z', 100)); // 300 bytes
        Assert.Equal(DetectionActionKind.None, first.Kind);

        var second = FeedBlock(d, deltas: 3, payload: new string('z', 100)); // +300 → 600 > 500
        Assert.Equal(DetectionActionKind.Abort, second.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    // ── Per-block count resets at content_block_start ────────────────────────────

    [Fact]
    public void CountBudget_ResetsPerBlock_NoFalseTripAcrossModerateBlocks()
    {
        var ctx = Ctx();
        // Count budget 10, big byte budget. Two blocks of 8 deltas each: neither
        // block exceeds 10, and because the count resets at content_block_start the
        // 16 total must NOT trip.
        var d = Detector(ctx, new RunawayGuardOptions { MaxDeltaCount = 10, MaxDeltaBytes = long.MaxValue });

        var first = FeedBlock(d, deltas: 8, payload: "x");
        var second = FeedBlock(d, deltas: 8, payload: "x");

        Assert.Equal(DetectionActionKind.None, first.Kind);
        Assert.Equal(DetectionActionKind.None, second.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Normal stream passes ─────────────────────────────────────────────────────

    [Fact]
    public void NormalStream_PassesUnderProductionDefaults()
    {
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions()); // shipping defaults (12 MiB / 20k)

        // A realistic small response: a handful of normal-sized deltas.
        var action = FeedBlock(d, deltas: 200, payload: new string('a', 256));

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Non-delta events are ignored ─────────────────────────────────────────────

    [Fact]
    public void NonDeltaEvents_DoNotCount()
    {
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions { MaxDeltaCount = 1, MaxDeltaBytes = 10 });

        // message_start / message_delta / message_stop carry bytes but are not
        // content_block_delta — they must not move either budget.
        Assert.Equal(DetectionActionKind.None,
            d.InspectEvent(new SseItem<string>(new string('b', 100), "message_start")).Kind);
        Assert.Equal(DetectionActionKind.None,
            d.InspectEvent(new SseItem<string>(new string('b', 100), "message_delta")).Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Count budget disabled (<= 0) still lets the byte budget run ──────────────

    [Fact]
    public void CountBudgetDisabled_ByteBudgetStillApplies()
    {
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions { MaxDeltaCount = 0, MaxDeltaBytes = 250 });

        // MaxDeltaCount <= 0 disables the count check; the byte budget still trips
        // (3×100 = 300 > 250).
        var action = FeedBlock(d, deltas: 3, payload: new string('c', 100));

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    // ── Signal selects error type / status ───────────────────────────────────────

    [Fact]
    public void ApiErrorSignal_Emits500ApiError()
    {
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            MaxDeltaCount = 5,
            MaxDeltaBytes = long.MaxValue,
            Signal = ResponseDetectionSignal.ApiError,
        });

        var action = FeedBlock(d, deltas: 6, payload: "x");

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.Equal(500, action.HttpStatus);
        Assert.Contains("api_error", action.ErrorJson!);
    }

    // ── Enabled flag reflects config ─────────────────────────────────────────────

    [Fact]
    public void Enabled_FollowsConfig()
    {
        var ctx = Ctx();
        Assert.True(Detector(ctx, new RunawayGuardOptions { Enabled = true }).Enabled);
        Assert.False(Detector(ctx, new RunawayGuardOptions { Enabled = false }).Enabled);
    }

    // ── Repetition-density signal ────────────────────────────────────────────────
    // Grounded in trace 20260707-043031-0007: claude-opus-4.8 repeated one token
    // ~32,000x to max_tokens in ~1,010 deltas / ~500 KB — under both volume budgets.
    // trailing-500-token unique ratio was ~0.002 (runaway) vs ~0.88 (normal output).

    // A content_block_delta text payload carrying `text` as one text_delta. Callers
    // that pass a whole token + trailing whitespace exercise the whole-token path;
    // Repetition_TokenSplitAcrossManyDeltas passes one char at a time to exercise the
    // _tokenTail carry-over across delta boundaries.
    private static SseItem<string> TextDelta(string text) =>
        new("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":"
            + System.Text.Json.JsonSerializer.Serialize(text) + "}}", "content_block_delta");

    // Options that isolate the repetition-DENSITY signal: volume budgets effectively
    // off, and the consecutive-run signal off (RepetitionMaxConsecutiveRepeat = 0) so a
    // repeated-token flood exercises ONLY the window/ratio path under test here. The
    // run-length signal has its own dedicated tests.
    private static RunawayGuardOptions RepOpts(int window = 500, double ratio = 0.05) => new()
    {
        MaxDeltaCount = int.MaxValue,
        MaxDeltaBytes = long.MaxValue,
        RepetitionWindow = window,
        RepetitionMinUniqueRatio = ratio,
        RepetitionMaxConsecutiveRepeat = 0,
    };

    [Fact]
    public void Repetition_SingleTokenLoop_Trips_UnderVolumeBudgets()
    {
        // Contract: a single repeated token past the window trips the repetition signal
        // even though neither the delta-count nor the byte budget is exceeded.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        // Stream the runaway shape: "court\n\n" fragments (like the real trace).
        for (var i = 0; i < 200 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("court\n\n"));
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
        Assert.Contains("overloaded_error", action.ErrorJson!);
    }

    [Fact]
    public void Repetition_DiverseProse_DoesNotTrip()
    {
        // Contract: a long, linguistically diverse block stays well above the ratio
        // floor and must never trip. 300 distinct words over a 50-token window keeps
        // the trailing-window unique ratio near 1.0.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 300 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta($"word{i} "));
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_RepetitiveButShorterThanWindow_DoesNotTrip()
    {
        // Contract: the window must be FULL before the ratio is evaluated, so a brief
        // legitimate repetition (fewer than window tokens) is not a false positive.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 100));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        // Only 40 repeated tokens — below the 100-token window.
        for (var i = 0; i < 40 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("yes "));
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_DisabledByNonPositiveWindow_NeverTrips()
    {
        // Contract: RepetitionWindow <= 0 disables the signal; the byte/count budgets
        // still function (here they're off, so nothing trips at all).
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 0));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 2000 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("court\n\n"));
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_PerBlockReset_DiverseBlockDoesNotInheritRepetitiveBlock()
    {
        // Contract: the window resets at each content_block_start, so a repetitive
        // block that ends without tripping cannot poison a following diverse block.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 100));

        // Block 0: repetitive but only 40 tokens (under window) -> no trip.
        d.InspectEvent(BlockStart(0));
        for (var i = 0; i < 40; i++) d.InspectEvent(TextDelta("yes "));

        // Block 1: diverse. If the window had NOT reset, the leftover "yes" tokens
        // would drag the ratio down; with reset, block 1 is judged on its own.
        DetectionAction action = d.InspectEvent(BlockStart(1));
        for (var i = 0; i < 300 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta($"token{i} "));
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_TwoTokenAlternation_Trips()
    {
        // Contract: the ratio signal (unlike a run-length counter) catches an A B A B
        // alternation — 2 distinct tokens over a 50-window is ratio 0.04 < 0.05.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50, ratio: 0.05));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 200 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta(i % 2 == 0 ? "ping " : "pong "));
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_NonTextDeltas_DoNotFeedWindow()
    {
        // Contract: only text_delta/thinking_delta feed the window. A stream of
        // input_json_delta (a tool call) must not trip the repetition signal even if
        // its raw payloads repeat — those are volume-budget territory, not repetition.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 500 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"x\"}}",
                "content_block_delta"));
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Ratio out-of-range disables the signal (config-typo safety) ──────────────

    [Theory]
    [InlineData(1.0)]   // >= 1 would trip on EVERY full window (distinct <= window)
    [InlineData(1.5)]
    [InlineData(0.0)]   // <= 0 is dead config
    [InlineData(-0.1)]
    public void Repetition_OutOfRangeRatio_DisablesSignal_NeverTrips(double ratio)
    {
        // Contract: an out-of-range RepetitionMinUniqueRatio must NOT force-abort. A
        // ratio >= 1 in particular would otherwise trip on any full window; treat it
        // (and <= 0) as disabling the repetition signal.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50, ratio: ratio));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 500 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("court\n\n"));
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Whitespace-free run does not blow up the carried tail ────────────────────

    [Fact]
    public void Repetition_WhitespaceFreeRun_DoesNotGrowTailUnbounded_AndDoesNotFalseTrip()
    {
        // Contract: a long whitespace-free run (base64/minified/CJK) is a single token
        // that never completes, so it must not trip the repetition signal, and the
        // carried tail must stay bounded (no quadratic reallocation). We can't measure
        // memory here, but a single unbroken 100k-char token across 1000 deltas must
        // (a) never trip (one token = never fills the window) and (b) complete promptly,
        // which it cannot if the tail grows to 100k and reallocates each delta.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 1000 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta(new string('A', 100))); // no whitespace, ever
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_TokenSplitAcrossManyDeltas_CountedOnce_TripsOnRepeatedWord()
    {
        // Contract: a token whose characters arrive across MULTIPLE deltas is counted
        // exactly once (carry-over correctness). Stream "elephant " one char per delta,
        // repeated — it must trip (one distinct token) once the window fills, proving
        // the char-fragment carry-over reassembles the same token rather than many
        // spurious distinct ones.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        const string word = "elephant ";
        for (var w = 0; w < 200 && action.Kind == DetectionActionKind.None; w++)
        {
            foreach (var ch in word)
            {
                action = d.InspectEvent(TextDelta(ch.ToString()));
                if (action.Kind != DetectionActionKind.None) break;
            }
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_RepeatedTokenAllocatesFarLessThanDistinct_SpanLookupReusesKey()
    {
        // Contract (allocation): the span-keyed tokenizer materializes a substring only
        // for a token that is NEW to the window. Packing many tokens into each delta makes
        // the per-token key cost dominate the fixed per-delta cost (the JSON string +
        // ExtractDeltaText). A delta full of ONE repeated token then allocates ~one key,
        // while a delta full of DISTINCT tokens allocates one key each. (The prior
        // Split-based tokenizer allocated a substring per token in BOTH cases and would
        // fail this — the test guards the optimization, not just correctness.)
        const int tokensPerDelta = 2000;
        const int deltas = 20;

        long Measure(Func<int, string> tokenAt)
        {
            var ctx = Ctx();
            var d = Detector(ctx, RepOpts(window: 100_000, ratio: 0.0001)); // never trips
            d.InspectEvent(BlockStart(0));
            var payloads = new string[deltas];
            for (var k = 0; k < deltas; k++)
            {
                var sb = new System.Text.StringBuilder();
                for (var j = 0; j < tokensPerDelta; j++) sb.Append(tokenAt(k * tokensPerDelta + j)).Append(' ');
                payloads[k] = sb.ToString();
            }
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var p in payloads) d.InspectEvent(TextDelta(p));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Measure(_ => "warm"); // warm up JIT/first-call paths

        var repeated = Measure(_ => "same");        // 1 distinct token overall, reused
        var distinct = Measure(i => "t" + i);       // all distinct tokens

        // With the fixed per-delta cost amortized across 2000 tokens, the repeated run must
        // allocate far below half the distinct run — the difference is the per-token key
        // substrings the span lookup avoids.
        Assert.True(repeated * 2 < distinct,
            $"repeated={repeated} bytes should be far below distinct={distinct} bytes (span lookup must reuse keys)");
    }

    [Fact]
    public void Repetition_ThinkingDelta_AlsoFeedsWindow()
    {
        // Contract: thinking_delta text feeds the window too (not just text_delta), so a
        // repetition loop inside a thinking block is caught.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 200 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":"
                + System.Text.Json.JsonSerializer.Serialize("hmm ") + "}}", "content_block_delta"));
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_DiversePrefixThenRepeatedToken_TripsOnlyAfterWindowRollsOver()
    {
        // Contract (exercises ring EVICTION): fill the window with `window` DISTINCT
        // tokens (no trip — distinct == window), then stream one repeated token. The
        // signal must trip only once the window has fully rolled over to that token,
        // which requires eviction to decrement the evicted tokens out of the multiset.
        // A broken eviction (never decrements) would keep distinct high and never trip.
        const int window = 30;
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: window, ratio: 0.2)); // floor = 6 distinct
        d.InspectEvent(BlockStart(0));

        // Fill with 30 distinct tokens — must NOT trip (distinct == window).
        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < window; i++)
        {
            action = d.InspectEvent(TextDelta($"w{i} "));
            Assert.Equal(DetectionActionKind.None, action.Kind);
        }

        // Now repeat one token. As it evicts the distinct prefix, the distinct count
        // falls; once distinct < 6 the guard trips. It must trip within ~window pushes.
        for (var i = 0; i < window + 5 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("same "));
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Theory]
    [InlineData(6, false)]  // distinct == floor (window*ratio = 30*0.2 = 6): NOT < floor -> no trip
    [InlineData(5, true)]   // distinct == floor-1: < floor -> trip
    public void Repetition_RatioBoundary_IsStrictLessThan(int distinctTokens, bool expectTrip)
    {
        // Contract: the trip condition is `distinct < window*ratio` (strict). With
        // window 30 and ratio 0.2 the floor is exactly 6, so 6 distinct must NOT trip
        // and 5 distinct must. Pins the strict `<` against a `<=` mutation.
        const int window = 30;
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: window, ratio: 0.2));
        d.InspectEvent(BlockStart(0));

        // Fill the window cycling through exactly `distinctTokens` distinct values so the
        // full window holds that many distinct tokens.
        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < window * 3 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta($"t{i % distinctTokens} "));
        }

        Assert.Equal(expectTrip ? DetectionActionKind.Abort : DetectionActionKind.None, action.Kind);
        Assert.Equal(expectTrip, ctx.RunawayDetected);
    }

    [Theory]
    [InlineData(50, true)]   // exactly window identical tokens -> full -> trips
    [InlineData(49, false)]  // one short of window -> not full -> no trip
    public void Repetition_FullnessBoundary_TripsAtExactlyWindow(int count, bool expectTrip)
    {
        // Contract: the fullness gate is `_ringCount >= window` (strict boundary). Exactly
        // `window` identical tokens fills the window and trips; `window-1` does not.
        const int window = 50;
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: window));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < count && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("x "));
        }

        Assert.Equal(expectTrip ? DetectionActionKind.Abort : DetectionActionKind.None, action.Kind);
        Assert.Equal(expectTrip, ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_AbsurdWindow_IsClamped_NoHugeAllocation()
    {
        // Contract: a fat-fingered RepetitionWindow (e.g. an extra few zeros) must be
        // clamped so the per-request ring array can't OOM the bridge. Constructing +
        // Begin() with a billion-token window must not throw / allocate ~GBs; the signal
        // still functions (a repeated token trips) under the clamped 100k window.
        var ctx = Ctx();
        var d = Detector(ctx, RepOpts(window: 1_000_000_000, ratio: 0.05));
        d.InspectEvent(BlockStart(0));

        // Fill past the clamped window efficiently: 1000 tokens per delta, ~101 deltas
        // fills 100k. (Feeding one token per delta would be 100k JSON parses.)
        var chunk = string.Concat(System.Linq.Enumerable.Repeat("x ", 1000));
        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 102 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta(chunk));
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void Repetition_AllWhitespaceDelta_FlushesPendingTailToken()
    {
        // Contract (tail carry-over): a token split so that a WHITESPACE-ONLY delta
        // supplies its terminating boundary must be counted exactly once — the pending
        // partial ("cou") is completed by the "\n\n" delta, not dropped or doubled. We
        // assert the same trip timing as if the word had arrived whole: feeding
        // "cou" then "\n\n" then repeating must trip identically to feeding "court "
        // repeatedly. Uses a tiny window so the token count to trip is small.
        static int TripAt(Func<int, string[]> deltasForWord)
        {
            var ctx = Ctx();
            var d = Detector(ctx, RepOpts(window: 10, ratio: 0.15)); // trips at 1 distinct, window full
            d.InspectEvent(BlockStart(0));
            var count = 0;
            for (var w = 0; w < 100; w++)
            {
                foreach (var frag in deltasForWord(w))
                {
                    count++;
                    if (d.InspectEvent(TextDelta(frag)).Kind == DetectionActionKind.Abort)
                    {
                        return count; // number of deltas fed until the trip
                    }
                }
            }
            return -1; // never tripped
        }

        // Whole word (one delta) vs split-with-whitespace-flush ("cou" then "\n\n").
        var whole = TripAt(_ => new[] { "court " });
        var splitFlush = TripAt(_ => new[] { "cou", "\n\n" });

        Assert.True(whole > 0, "whole-word feed should trip");
        Assert.True(splitFlush > 0, "whitespace-flushed split feed should trip (tail flushed, not dropped)");
        // Both accumulate the SAME "court" tokens; the split feed just uses 2 deltas per
        // word instead of 1, so it trips after twice as many deltas — proving each word is
        // counted once (if the tail were dropped it would never trip; if doubled, sooner).
        Assert.Equal(whole * 2, splitFlush);
    }

    [Fact]
    public void Repetition_EmptyDeltaBetweenFragments_PreservesPendingTail()
    {
        // Contract (ordering): an empty-text delta arriving mid-token must NOT clear the
        // carried partial. Feeding "cou", then an empty delta, then "rt " must reassemble
        // "court" — identical to feeding "court " directly. Guards the FeedRepetition
        // early-return-before-tail-reset ordering.
        static int TripAt(Func<string[]> frags)
        {
            var ctx = Ctx();
            var d = Detector(ctx, RepOpts(window: 10, ratio: 0.15));
            d.InspectEvent(BlockStart(0));
            var words = 0;
            for (var w = 0; w < 100; w++)
            {
                foreach (var frag in frags())
                {
                    if (d.InspectEvent(TextDelta(frag)).Kind == DetectionActionKind.Abort)
                    {
                        return words + 1; // words completed at trip
                    }
                }
                words++;
            }
            return -1;
        }

        // An empty delta ("") carries no text (ExtractDeltaText -> empty) and must leave
        // the pending "cou" tail intact so "rt " completes "court".
        var withEmpty = TripAt(() => new[] { "cou", "", "rt " });
        var direct = TripAt(() => new[] { "court " });

        Assert.True(direct > 0, "direct feed should trip");
        Assert.True(withEmpty > 0, "feed with an interleaved empty delta should still trip (tail preserved)");
        // Same number of "court" words needed to fill the window and trip either way.
        Assert.Equal(direct, withEmpty);
    }

    // ── Run-length signal (consecutive identical tokens) ─────────────────────────

    // Options that isolate the run-length signal: volume budgets off, density window off
    // (RepetitionWindow = 0), so only the consecutive-run threshold is live.
    private static RunawayGuardOptions RunLenOpts(int maxConsecutive = 50) => new()
    {
        MaxDeltaCount = int.MaxValue,
        MaxDeltaBytes = long.MaxValue,
        RepetitionWindow = 0,               // density signal off
        RepetitionMaxConsecutiveRepeat = maxConsecutive,
    };

    [Theory]
    [InlineData(50, true)]   // exactly the threshold of consecutive repeats -> trips
    [InlineData(49, false)]  // one short of the threshold -> no trip
    public void RunLength_TripsAtExactlyThreshold_IndependentOfWindow(int repeats, bool expectTrip)
    {
        // Contract: the run-length signal trips when the SAME token repeats
        // RepetitionMaxConsecutiveRepeat times in a row, with the density window DISABLED
        // (RepetitionWindow=0) — proving it does not depend on the window filling. A short
        // flood the 500-window can never see is caught here.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < repeats && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("count "));
        }

        Assert.Equal(expectTrip ? DetectionActionKind.Abort : DetectionActionKind.None, action.Kind);
        Assert.Equal(expectTrip, ctx.RunawayDetected);
    }

    [Fact]
    public void RunLength_ShortFlood_TripsEvenWhenWindowNeverFills()
    {
        // Contract (the `count` capture, Bug B): a body of ~100 identical tokens is far
        // shorter than the default 500-token window, so the density signal is immune. With
        // BOTH signals at their defaults (window 500, run-length 50), the flood must still
        // trip — via run-length. Regression for the real 108-token `count` capture.
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            MaxDeltaCount = int.MaxValue,
            MaxDeltaBytes = long.MaxValue,
            RepetitionWindow = 500,               // default — cannot fill on a 100-token body
            RepetitionMinUniqueRatio = 0.05,
            RepetitionMaxConsecutiveRepeat = 50,  // default
        });
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 100 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("count "));
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void RunLength_InterruptedRun_ResetsAndDoesNotTrip()
    {
        // Contract: a different token resets the consecutive run to one, so only an
        // UNINTERRUPTED run reaching the threshold trips. 40 "a" then one "b" then 40 "a"
        // is two runs of 40, never 50 in a row -> no trip.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 40 && action.Kind == DetectionActionKind.None; i++)
            action = d.InspectEvent(TextDelta("a "));
        if (action.Kind == DetectionActionKind.None)
            action = d.InspectEvent(TextDelta("b "));      // interrupts the run
        for (var i = 0; i < 40 && action.Kind == DetectionActionKind.None; i++)
            action = d.InspectEvent(TextDelta("a "));

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void RunLength_DiverseOutput_DoesNotTrip()
    {
        // Contract: a linguistically diverse stream never repeats a token >2-3 in a row,
        // so the run-length signal must stay silent across a large output.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 2000 && action.Kind == DetectionActionKind.None; i++)
            action = d.InspectEvent(TextDelta($"word{i % 37} "));

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void RunLength_DisabledByNonPositiveThreshold_NeverTrips()
    {
        // Contract: RepetitionMaxConsecutiveRepeat <= 0 disables the run-length signal.
        // With the density window also off, a huge identical-token flood must pass.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 0));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 1000 && action.Kind == DetectionActionKind.None; i++)
            action = d.InspectEvent(TextDelta("count "));

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void RunLength_RunSplitAcrossDeltas_CountedConsecutive()
    {
        // Contract: a run whose tokens are split at their terminating whitespace across
        // delta boundaries is reassembled (via the carried tail) and counted as one
        // uninterrupted run. Feed "count" and "\n\n" as separate deltas per repeat — the
        // token "count" only completes when the whitespace delta lands, yet 50 such
        // reassembled repeats must trip exactly like 50 whole "count " deltas.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 50 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta("count"));
            if (action.Kind == DetectionActionKind.None)
                action = d.InspectEvent(TextDelta("\n\n"));
        }

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void RunLength_ResetsPerContentBlock()
    {
        // Contract: the consecutive-run counter resets at each content_block_start, so a
        // run spanning a block boundary does not accumulate. 40 "a" in block 0 then 40 "a"
        // in block 1 is two runs of 40, never 50 in a row within one block -> no trip.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50));

        DetectionAction action = d.InspectEvent(BlockStart(0));
        for (var i = 0; i < 40 && action.Kind == DetectionActionKind.None; i++)
            action = d.InspectEvent(TextDelta("a "));
        if (action.Kind == DetectionActionKind.None)
            action = d.InspectEvent(BlockStart(1));            // new block resets the run
        for (var i = 0; i < 40 && action.Kind == DetectionActionKind.None; i++)
            action = d.InspectEvent(TextDelta("a "));

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Buffered (application/json) delivery path ────────────────────────────────

    private static byte[] Buffered(params (string type, string text)[] blocks)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"type\":\"message\",\"role\":\"assistant\",\"content\":[");
        for (var i = 0; i < blocks.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var (type, text) = blocks[i];
            if (type == "text")
                sb.Append("{\"type\":\"text\",\"text\":").Append(System.Text.Json.JsonSerializer.Serialize(text)).Append('}');
            else if (type == "thinking")
                sb.Append("{\"type\":\"thinking\",\"thinking\":").Append(System.Text.Json.JsonSerializer.Serialize(text)).Append('}');
            else if (type == "tool_use")
                sb.Append("{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"X\",\"input\":{}}");
        }
        sb.Append("]}");
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Repeat(string token, int n) =>
        string.Concat(System.Linq.Enumerable.Repeat(token, n));

    [Fact]
    public void Buffered_ShortFlood_TripsWithBufferStatus()
    {
        // Contract (Bug A + the `count` capture): Copilot returned a one-shot
        // application/json body — a text block that floods "count" 100x then a valid
        // tool_use. The buffered path (InspectBuffered) must abort with the configured
        // status. Before this change RunawayGuard had no InspectBuffered and relayed it.
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            MaxDeltaCount = int.MaxValue,
            MaxDeltaBytes = long.MaxValue,
            RepetitionWindow = 500,
            RepetitionMinUniqueRatio = 0.05,
            RepetitionMaxConsecutiveRepeat = 50,
            Signal = ResponseDetectionSignal.OverloadedError,
        });

        var body = Buffered(("text", "here is the count\n\n" + Repeat("count\n\n", 100)), ("tool_use", ""));
        var action = d.InspectBuffered(body);

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.Equal(529, action.HttpStatus);   // OverloadedError -> HTTP 529
        Assert.True(ctx.RunawayDetected);
        Assert.DoesNotContain("count", action.ErrorJson);  // no leaked content in the error
    }

    [Fact]
    public void Buffered_CleanResponse_PassesThrough()
    {
        // Contract: a buffered response within all thresholds yields None (delivered
        // unchanged), and does not set the runaway flag.
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            RepetitionWindow = 500,
            RepetitionMinUniqueRatio = 0.05,
            RepetitionMaxConsecutiveRepeat = 50,
        });

        var body = Buffered(("text", "This is a perfectly ordinary and diverse answer with varied words."), ("tool_use", ""));
        var action = d.InspectBuffered(body);

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Buffered_UnparseableBody_FailsOpen()
    {
        // Contract: a body that is not parseable Anthropic JSON must fail open (None),
        // never turning a real response into an error on a parse hiccup.
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions { RepetitionMaxConsecutiveRepeat = 50 });

        var action = d.InspectBuffered(System.Text.Encoding.UTF8.GetBytes("not json at all <<<"));

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Buffered_NoContentArray_FailsOpen()
    {
        // Contract: a JSON object without a `content` array is not a scannable Messages
        // body; fail open rather than throw.
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions { RepetitionMaxConsecutiveRepeat = 50 });

        var action = d.InspectBuffered(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"message\"}"));

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Buffered_FloodInThinkingBlock_Trips()
    {
        // Contract: the buffered scan covers thinking blocks like the streaming path
        // (RunawayGuard has no thinking-scan gate). A flood inside a thinking block trips.
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            RepetitionWindow = 500,
            RepetitionMinUniqueRatio = 0.05,
            RepetitionMaxConsecutiveRepeat = 50,
        });

        var body = Buffered(("thinking", Repeat("hmm\n\n", 80)), ("text", "ok"));
        var action = d.InspectBuffered(body);

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void Buffered_PerBlockReset_EarlierBlockDoesNotPoisonLater()
    {
        // Contract: each buffered block is its own scope (reset between blocks), mirroring
        // the streaming per-content_block_start reset. A block ending in 40 "a" followed by
        // a clean block must NOT trip — the run does not carry across the block boundary.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50));

        var body = Buffered(
            ("text", Repeat("a ", 40)),                 // 40 in a row, under threshold
            ("text", "a diverse and clean second block"));
        var action = d.InspectBuffered(body);

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    [Fact]
    public void Buffered_LowDiversityNoConsecutiveRepeat_TripsOnDensity()
    {
        // Contract (parity — density on the buffered path): the buffered scan must route
        // through the SAME per-block core as streaming, so the DENSITY signal (not only
        // run-length) trips on a buffered body. A long 2-token alternation fills the 500
        // window with 2 distinct tokens (ratio 0.004 < 0.05) but never repeats a token
        // twice in a row, so run-length (50) CANNOT fire — only density can. This is the
        // long ~500KB/32000-repeat opus-4.8 shape delivered as one-shot JSON.
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            MaxDeltaBytes = long.MaxValue,
            MaxDeltaCount = int.MaxValue,
            RepetitionWindow = 500,
            RepetitionMinUniqueRatio = 0.05,
            RepetitionMaxConsecutiveRepeat = 50,   // enabled, but cannot fire (max run = 1)
        });

        var body = Buffered(("text", Repeat("ping pong ", 300)));  // 600 tokens, max consecutive run = 1
        var action = d.InspectBuffered(body);

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.True(ctx.RunawayDetected);
    }

    [Fact]
    public void RunLength_ShorterTokenAfterLongerPrefixToken_DoesNotFalseMatch()
    {
        // Contract (the _prevTokenBuf length guard): equality is
        // `_prevTokenLen == token.Length && SequenceEqual(...)`. The reused buffer is
        // never cleared, so the LENGTH check is the only thing stopping a shorter token
        // from matching the stale prefix of a longer previous token. Alternating
        // "counter"/"count" (never identical, but "count" is a prefix of "counter") must
        // reset the run every token → never trips. If the length conjunct were dropped,
        // "count" would match _prevTokenBuf[0..5]=="count" and the run would grow falsely.
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50));
        d.InspectEvent(BlockStart(0));

        DetectionAction action = DetectionAction.None;
        for (var i = 0; i < 200 && action.Kind == DetectionActionKind.None; i++)
        {
            action = d.InspectEvent(TextDelta(i % 2 == 0 ? "counter " : "count "));
        }

        Assert.Equal(DetectionActionKind.None, action.Kind);
        Assert.False(ctx.RunawayDetected);
    }

    // ── Observability: one Warning naming reason + delivery mode, no content ──────

    private static RunawayGuardDetector Detector(BridgeContext<MessagesRequest> ctx, RunawayGuardOptions opts, ILogger<RunawayGuardDetector> log)
    {
        var d = new RunawayGuardDetector(new DetectorOrder<RunawayGuardDetector>(3), TestOptions.Snapshot(opts), ctx, log);
        d.Begin();
        return d;
    }

    [Fact]
    public void Trip_Streaming_LogsExactlyOneWarning_WithDeliveryStream_NoContent()
    {
        // Contract: a streaming runaway trip emits exactly one Warning naming the reason
        // and delivery=stream, and never the runaway token itself.
        var log = new CapturingLogger();
        var ctx = Ctx();
        var d = Detector(ctx, RunLenOpts(maxConsecutive: 50), log);
        d.InspectEvent(BlockStart(0));
        for (var i = 0; i < 50; i++) d.InspectEvent(TextDelta("secrettoken "));

        var warnings = log.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("delivery=stream", warnings[0].Rendered);
        Assert.DoesNotContain("secrettoken", warnings[0].Rendered); // no runaway content
    }

    [Fact]
    public void Trip_Buffered_LogsExactlyOneWarning_WithDeliveryBuffer_NoContent()
    {
        // Contract: a buffered runaway trip emits exactly one Warning naming delivery=buffer.
        var log = new CapturingLogger();
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            RepetitionWindow = 500,
            RepetitionMinUniqueRatio = 0.05,
            RepetitionMaxConsecutiveRepeat = 50,
        }, log);

        d.InspectBuffered(Buffered(("text", Repeat("secrettoken\n\n", 60)), ("tool_use", "")));

        var warnings = log.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("delivery=buffer", warnings[0].Rendered);
        Assert.DoesNotContain("secrettoken", warnings[0].Rendered);
    }

    [Fact]
    public void Trip_BothSignalsCouldFire_RunLengthWins_AttributedInReason()
    {
        // Contract (signal precedence + attribution): when a single repeated token could
        // trip BOTH density (small window) and run-length, run-length is evaluated first
        // (before the density push), so it wins and the operator-facing reason names the
        // consecutive-run cause — not the window ratio. Pins the ordering so a refactor
        // that moved the density check ahead would flip the attribution and go red.
        var log = new CapturingLogger();
        var ctx = Ctx();
        var d = Detector(ctx, new RunawayGuardOptions
        {
            MaxDeltaBytes = long.MaxValue,
            MaxDeltaCount = int.MaxValue,
            RepetitionWindow = 50,
            RepetitionMinUniqueRatio = 0.05,
            RepetitionMaxConsecutiveRepeat = 50,
        }, log);
        d.InspectEvent(BlockStart(0));
        for (var i = 0; i < 50; i++) d.InspectEvent(TextDelta("x "));

        var warnings = log.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("in a row", warnings[0].Rendered);                 // run-length reason
        Assert.DoesNotContain("unique of the trailing", warnings[0].Rendered); // NOT density
    }

    private sealed class CapturingLogger : ILogger<RunawayGuardDetector>
    {
        public readonly List<(LogLevel Level, string Rendered)> Records = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

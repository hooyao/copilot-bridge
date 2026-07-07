using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
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

    // Options that isolate the repetition signal: volume budgets effectively off.
    private static RunawayGuardOptions RepOpts(int window = 500, double ratio = 0.05) => new()
    {
        MaxDeltaCount = int.MaxValue,
        MaxDeltaBytes = long.MaxValue,
        RepetitionWindow = window,
        RepetitionMinUniqueRatio = ratio,
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
}

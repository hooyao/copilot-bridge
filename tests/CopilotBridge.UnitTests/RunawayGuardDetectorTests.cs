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
            Signal = ResponseLeakSignal.ApiError,
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
}

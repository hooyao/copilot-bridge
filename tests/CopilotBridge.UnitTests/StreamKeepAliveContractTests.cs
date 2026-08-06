using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract coverage for downstream keepalive injection
/// (<c>Pipeline:UpstreamTimeout:KeepAliveIntervalSeconds</c>).
///
/// <para>The contract, in words: Copilot sends no keepalive while a model is
/// thinking, so an Anthropic client's own idle watchdogs would end a healthy
/// long-thinking turn. The bridge therefore injects the <c>ping</c> the upstream
/// omits WHILE UPSTREAM IS SILENT — and, critically, those injected pings must not
/// buy the upstream any more time, because they are exactly what stops the client
/// from judging silence. If they also reset the bridge's stream-idle budget, nothing
/// would be judging it and a hung upstream would stream pings forever.</para>
///
/// <para>The reader-level tests drive <see cref="StreamIdleReader"/> directly, which
/// takes <see cref="TimeSpan"/>s and so can express the millisecond-scale timing the
/// deadline contract is about. The strategy-level tests use whole seconds (the
/// configuration surface's unit) and stay coarse deliberately: they assert the
/// inject-vs-relay decision, not the clock.</para>
/// </summary>
public class StreamKeepAliveContractTests
{
    // ── Building blocks ───────────────────────────────────────────────────────

    private static UpstreamTimeoutOptions Timeouts(int streamIdleSeconds, int keepAliveSeconds) => new()
    {
        FirstByteTimeoutSeconds = 0,
        StreamIdleTimeoutSeconds = streamIdleSeconds,
        KeepAliveIntervalSeconds = keepAliveSeconds,
    };

    private static BridgeContext<MessagesRequest> Ctx(CancellationToken ct = default) => new()
    {
        Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/cc/v1/messages",
            Body = new MessagesRequest
            {
                Model = "claude-opus-4-8",
                Messages = Array.Empty<MessageParam>(),
                Stream = true,
            },
        },
        Response = new BridgeResponse(),
        Ct = ct,
    };

    private sealed class StubClient(HttpResponseMessage resp) : ICopilotClient
    {
        public ValueTask<HttpResponseMessage> PostMessagesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            IReadOnlyList<string>? anthropicBeta = null,
            IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
            CancellationToken ct = default) => new(resp);
        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) => new(resp);
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static HttpResponseMessage StreamingResponse(Stream body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    private static byte[] Delta(string text) => Encoding.UTF8.GetBytes(
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}\n\n");

    private static async Task<List<SseItem<string>>> ForwardAndDrainAsync(
        Stream upstream, UpstreamTimeoutOptions t, RequestAudit? audit = null)
    {
        var ctx = Ctx();
        var strategy = new CopilotMessagesPassthroughStrategy(
            new StubClient(StreamingResponse(upstream)), ctx, audit ?? TestAudit.Create(false),
            Options.Create(t), NullLogger<CopilotMessagesPassthroughStrategy>.Instance);
        await strategy.ForwardAsync();
        Assert.NotNull(ctx.Response.EventStream);
        var items = new List<SseItem<string>>();
        await foreach (var e in ctx.Response.EventStream!) items.Add(e);
        return items;
    }

    private static bool IsPing(SseItem<string> e) => e.EventType == "ping";

    /// <summary>An SSE enumerator that yields <paramref name="count"/> events, each after <paramref name="gap"/>.</summary>
    private static async IAsyncEnumerable<SseItem<string>> PacedEvents(int count, TimeSpan gap)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Delay(gap);
            yield return new SseItem<string>($"e{i}", "content_block_delta");
        }
    }

    /// <summary>An SSE enumerator that never yields — an upstream that went silent and stayed silent.</summary>
    private static async IAsyncEnumerable<SseItem<string>> NeverYields(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break; // unreachable
    }

    /// <summary>
    /// Builds a reader wired the way production wires it: the read CTS is LINKED to
    /// the client token, so a client cancel ends the pending upstream read. The reader
    /// documents that requirement; a bare (unlinked) source would leave a cancelled
    /// request waiting forever on a read nothing can interrupt.
    /// </summary>
    private static StreamIdleReader Reader(
        IAsyncEnumerable<SseItem<string>> src, CancellationTokenSource readCts,
        TimeSpan idle, TimeSpan keepAlive, out IAsyncEnumerator<SseItem<string>> e)
    {
        e = src.GetAsyncEnumerator(readCts.Token);
        return new StreamIdleReader(e, readCts, idle, keepAlive);
    }

    /// <summary>
    /// Serves <paramref name="prefix"/>, then goes silent for <paramref name="silence"/>
    /// (honouring cancellation, as a real socket read does), then serves
    /// <paramref name="suffix"/> and ends. Models "the model is thinking".
    /// </summary>
    private sealed class SilentGapStream(byte[] prefix, TimeSpan silence, byte[] suffix) : Stream
    {
        private int _pos;
        private bool _slept;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - _pos);
                prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
                return n;
            }
            if (!_slept)
            {
                await Task.Delay(silence, ct);
                _slept = true;
            }
            var off = _pos - prefix.Length;
            if (off >= suffix.Length) return 0; // EOF
            var m = Math.Min(buffer.Length, suffix.Length - off);
            suffix.AsSpan(off, m).CopyTo(buffer.Span);
            _pos += m;
            return m;
        }

        public override int Read(byte[] b, int o, int c) =>
            ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    // ── The load-bearing contract: pings must not buy upstream more time ──────

    /// <summary>
    /// Contract (LOAD-BEARING): a keepalive is an event the bridge SENT downstream,
    /// never one it RECEIVED upstream, so no number of keepalives may postpone the
    /// stream-idle timeout. With a keepalive interval far shorter than the budget and
    /// an upstream that never returns, the budget MUST still fire — and it must fire
    /// on schedule, measured from the last upstream event, after many pings were
    /// emitted.
    ///
    /// If this regresses, a hung upstream becomes invisible to every party at once:
    /// the client stopped judging silence because pings keep arriving, and the bridge
    /// stopped judging it because those same pings reset its own clock.
    /// </summary>
    [Fact]
    public async Task InjectedKeepAlives_DoNotPostponeTheIdleTimeout()
    {
        var idle = TimeSpan.FromMilliseconds(600);
        var keepAlive = TimeSpan.FromMilliseconds(50);
        using var readCts = new CancellationTokenSource();
        var reader = Reader(NeverYields(), readCts, idle, keepAlive, out var e);
        await using var _ = e;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var pings = 0;
        var ex = await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
        {
            while (true)
            {
                var outcome = await reader.MoveNextAsync(CancellationToken.None);
                if (outcome == StreamReadOutcome.KeepAliveDue)
                {
                    pings++;
                    // Bound the loop so the regression this guards FAILS rather than
                    // hangs: if keepalives reset the idle clock the timeout never comes,
                    // and an unbounded loop would take the whole test host down with it
                    // (an aborted run reads as infrastructure trouble, not as this bug).
                    Assert.True(started.Elapsed < idle * 5,
                        $"idle timeout never fired after {pings} keepalives over {started.Elapsed.TotalMilliseconds:0}ms "
                        + $"(budget {idle.TotalMilliseconds:0}ms) — injected keepalives are postponing the budget");
                    continue;
                }
                Assert.Fail($"upstream never emits, so only KeepAliveDue or a timeout is legal; got {outcome}");
            }
        });
        started.Stop();

        Assert.Equal(UpstreamTimeoutPhase.StreamIdle, ex.Phase);
        // Many keepalives were emitted during the silence...
        Assert.True(pings >= 5, $"expected the keepalive to tick repeatedly during the silence, got {pings}");
        // ...and the budget still fired on ITS OWN schedule, not pushed out by them.
        // Generous upper bound (3x) so a loaded CI box cannot flake it; the mutation
        // this guards (pings resetting the idle clock) never terminates at all.
        Assert.InRange(started.Elapsed, idle * 0.75, idle * 3);
    }

    /// <summary>
    /// Contract: a keepalive tick leaves the pending upstream read in flight — it is
    /// neither cancelled nor restarted — so the event that eventually arrives is still
    /// delivered exactly once, in order, after the pings.
    /// </summary>
    [Fact]
    public async Task KeepAliveTick_DoesNotDisturbThePendingRead()
    {
        // Wide interval-to-wait ratio (1s wait / 20ms interval = ~50 expected ticks) so a
        // loaded CI box cannot starve the assertion below. The contract is "the pending
        // read survives keepalive ticks", not a tick count, so only a LOWER bound of 1 is
        // asserted — enough to prove at least one tick was interleaved.
        var keepAlive = TimeSpan.FromMilliseconds(20);
        using var readCts = new CancellationTokenSource();
        // One event, arriving well after several keepalive intervals have elapsed.
        var reader = Reader(PacedEvents(1, TimeSpan.FromSeconds(1)), readCts,
            idle: TimeSpan.FromSeconds(30), keepAlive, out var e);
        await using var _ = e;

        var pings = 0;
        var events = new List<string>();
        while (true)
        {
            var outcome = await reader.MoveNextAsync(CancellationToken.None);
            if (outcome == StreamReadOutcome.KeepAliveDue) { pings++; continue; }
            if (outcome == StreamReadOutcome.EndOfStream) break;
            events.Add(e.Current.Data);
        }

        Assert.True(pings >= 1, $"expected at least one keepalive tick while the read was pending, got {pings}");
        Assert.Equal(["e0"], events);
    }

    /// <summary>
    /// Contract: the keepalive clock restarts from each genuine upstream event, so a
    /// stream that keeps emitting inside the interval is never padded with pings — the
    /// relayed sequence is exactly the upstream sequence.
    /// </summary>
    [Fact]
    public async Task UpstreamProgressing_EmitsNoKeepAlive()
    {
        using var readCts = new CancellationTokenSource();
        var reader = Reader(PacedEvents(5, TimeSpan.FromMilliseconds(10)), readCts,
            idle: TimeSpan.FromSeconds(30), keepAlive: TimeSpan.FromMilliseconds(500), out var e);
        await using var _ = e;

        var pings = 0;
        var events = 0;
        while (true)
        {
            var outcome = await reader.MoveNextAsync(CancellationToken.None);
            if (outcome == StreamReadOutcome.KeepAliveDue) { pings++; continue; }
            if (outcome == StreamReadOutcome.EndOfStream) break;
            events++;
        }

        Assert.Equal(5, events);
        Assert.Equal(0, pings);
    }

    /// <summary>
    /// Contract: keepalive disabled (&lt;= 0) means the reader never reports a tick,
    /// however long upstream stays silent — the idle budget is then the only deadline.
    /// </summary>
    [Fact]
    public async Task KeepAliveDisabled_NeverTicks()
    {
        using var readCts = new CancellationTokenSource();
        var reader = Reader(NeverYields(), readCts,
            idle: TimeSpan.FromMilliseconds(200), keepAlive: TimeSpan.Zero, out var e);
        await using var _ = e;

        var pings = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
        {
            while (true)
            {
                if (await reader.MoveNextAsync(CancellationToken.None) == StreamReadOutcome.KeepAliveDue) pings++;
                // Bounded so a regression FAILS rather than hanging the test host.
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(5),
                    $"idle timeout never fired; {pings} keepalive tick(s) seen");
            }
        });
        Assert.Equal(0, pings);
    }

    /// <summary>
    /// Contract: a keepalive interval at or above the stream-idle budget means the
    /// budget always fires first and no ping is ever due. Coherent configuration, not
    /// an error — but it must not produce a ping.
    /// </summary>
    [Fact]
    public async Task KeepAliveNotShorterThanBudget_NeverTicks()
    {
        using var readCts = new CancellationTokenSource();
        var reader = Reader(NeverYields(), readCts,
            idle: TimeSpan.FromMilliseconds(200), keepAlive: TimeSpan.FromMilliseconds(200), out var e);
        await using var _ = e;

        var pings = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
        {
            while (true)
            {
                if (await reader.MoveNextAsync(CancellationToken.None) == StreamReadOutcome.KeepAliveDue) pings++;
                // Bounded so a regression FAILS rather than hanging the test host.
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(5),
                    $"idle timeout never fired; {pings} keepalive tick(s) seen");
            }
        });
        Assert.Equal(0, pings);
    }

    /// <summary>
    /// Contract: a client cancel wins over both deadlines and surfaces as a plain
    /// cancellation — never as a bridge timeout, and never swallowed into a keepalive
    /// tick (which would leave the loop spinning on an aborted request).
    /// </summary>
    [Fact]
    public async Task ClientCancel_WinsOverKeepAliveAndIdle()
    {
        using var clientCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        // Linked exactly as production links it, so the client cancel reaches the read.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(clientCts.Token);
        var reader = Reader(NeverYields(), readCts,
            idle: TimeSpan.FromSeconds(30), keepAlive: TimeSpan.FromMilliseconds(20), out var e);
        await using var _ = e;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (true) await reader.MoveNextAsync(clientCts.Token);
        });
    }

    // ── Strategy level: what actually reaches the client ─────────────────────

    /// <summary>
    /// Contract: on the real /cc relay a silent upstream produces Anthropic-shaped
    /// <c>ping</c> events interleaved into the stream, and the late upstream event is
    /// still delivered. This is the end-to-end shape a client sees.
    /// </summary>
    [Fact]
    public async Task Strategy_SilentUpstream_YieldsPingsThenTheLateEvent()
    {
        // The gap is 5x the ping interval, not 2.5x. The contract pinned here is
        // "a silent upstream still produces repeated pings"; the margin must be wide
        // enough that a loaded CI runner losing a scheduling slice cannot turn it into
        // a failure. At 2.5x this went red on a shared runner while passing locally,
        // which tests timer luck rather than the behaviour.
        var upstream = new SilentGapStream(Delta("before"), TimeSpan.FromMilliseconds(5000), Delta("after"));
        var items = await ForwardAndDrainAsync(upstream, Timeouts(streamIdleSeconds: 30, keepAliveSeconds: 1));

        var pings = items.Where(IsPing).ToList();
        Assert.True(pings.Count >= 2, $"expected repeated pings during a ~5s silence at a 1s interval, got {pings.Count}");
        Assert.All(pings, p => Assert.Equal("{\"type\":\"ping\"}", p.Data));
        // Nothing upstream was lost or reordered: both deltas, in order, around the pings.
        var deltas = items.Where(i => !IsPing(i)).Select(i => i.Data).ToList();
        Assert.Equal(2, deltas.Count);
        Assert.Contains("before", deltas[0]);
        Assert.Contains("after", deltas[1]);
        Assert.True(items.FindIndex(IsPing) > 0, "a ping must never precede the first upstream event");
    }

    /// <summary>
    /// Contract: no keepalive before the stream's first upstream event. Injecting
    /// there would make an unstarted stream look started to the client; that phase
    /// belongs to the first-byte / stream-idle budgets alone.
    /// </summary>
    [Fact]
    public async Task Strategy_SilenceBeforeFirstEvent_YieldsNoPing()
    {
        // Silence FIRST (no prefix at all), then the one and only event.
        var upstream = new SilentGapStream([], TimeSpan.FromMilliseconds(2500), Delta("first"));
        var items = await ForwardAndDrainAsync(upstream, Timeouts(streamIdleSeconds: 30, keepAliveSeconds: 1));

        Assert.DoesNotContain(items, IsPing);
        Assert.Single(items);
        Assert.Contains("first", items[0].Data);
    }

    /// <summary>
    /// Contract: with injection disabled the relay is exactly the upstream sequence —
    /// the pre-keepalive behaviour, unchanged.
    /// </summary>
    [Fact]
    public async Task Strategy_KeepAliveDisabled_RelaysUpstreamUnchanged()
    {
        var upstream = new SilentGapStream(Delta("before"), TimeSpan.FromMilliseconds(1200), Delta("after"));
        var items = await ForwardAndDrainAsync(upstream, Timeouts(streamIdleSeconds: 30, keepAliveSeconds: 0));

        Assert.DoesNotContain(items, IsPing);
        Assert.Equal(2, items.Count);
    }

    // ── Observability: the artifact must stay honest ─────────────────────────

    /// <summary>
    /// Contract: the raw upstream capture records only bytes Copilot actually sent, so
    /// it must contain NO injected ping — that is what lets an operator diff it against
    /// the downstream record and see exactly where upstream went silent. The tee sits
    /// below the injection point precisely to make this structural.
    /// </summary>
    [Fact]
    public async Task RawUpstreamCapture_ContainsNoInjectedPing()
    {
        var upstream = new SilentGapStream(Delta("before"), TimeSpan.FromMilliseconds(2500), Delta("after"));
        var ctx = Ctx();
        var strategy = new CopilotMessagesPassthroughStrategy(
            new StubClient(StreamingResponse(upstream)), ctx, TestAudit.Create(true),
            Options.Create(Timeouts(streamIdleSeconds: 30, keepAliveSeconds: 1)),
            NullLogger<CopilotMessagesPassthroughStrategy>.Instance);
        await strategy.ForwardAsync();

        var relayed = new List<SseItem<string>>();
        await foreach (var e in ctx.Response.EventStream!) relayed.Add(e);
        Assert.Contains(relayed, IsPing); // the run really did inject

        var raw = Encoding.UTF8.GetString(ctx.Response.RawUpstreamRespBytesOrNull() ?? []);
        Assert.DoesNotContain("ping", raw);
        Assert.Contains("before", raw);
        Assert.Contains("after", raw);
    }

    /// <summary>
    /// Contract: a keepalive carries no message-state semantics, so the usage the
    /// bridge reports must be identical whether or not pings were interleaved. The
    /// relay loop feeds EVERY relayed event to the usage probe, so this is a real
    /// exposure, not a theoretical one — a ping that perturbed the snapshot would
    /// silently corrupt the reported token counts of any long-thinking turn.
    /// </summary>
    [Fact]
    public void InjectedPing_LeavesReportedUsageUnchanged()
    {
        const string messageStart =
            "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":11,\"output_tokens\":22,"
            + "\"cache_read_input_tokens\":33,\"cache_creation_input_tokens\":44}}}";

        var withoutPing = new UsageSnapshot();
        UsageProbe.TryUpdateFromStreamEvent("message_start", messageStart, withoutPing);

        var withPing = new UsageSnapshot();
        UsageProbe.TryUpdateFromStreamEvent("message_start", messageStart, withPing);
        var ping = StreamKeepAlive.Ping();
        UsageProbe.TryUpdateFromStreamEvent(ping.EventType, ping.Data, withPing);

        Assert.Equal(withoutPing.InputTokens, withPing.InputTokens);
        Assert.Equal(withoutPing.OutputTokens, withPing.OutputTokens);
        Assert.Equal(withoutPing.CacheReadInputTokens, withPing.CacheReadInputTokens);
        Assert.Equal(withoutPing.CacheCreationInputTokens, withPing.CacheCreationInputTokens);
        // Guard against the assertion passing because BOTH are empty.
        Assert.Equal(11, withPing.InputTokens);
        Assert.Equal(22, withPing.OutputTokens);
    }

    // ── Route-scoped eligibility: client protocol, not upstream strategy ─────

    private static BridgeContext<MessagesRequest> ResponsesCtx(string path) => new()
    {
        Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = path,
            Body = new MessagesRequest
            {
                Model = "gpt-5.6-sol",
                Messages = Array.Empty<MessageParam>(),
                Stream = true,
            },
        },
        Response = new BridgeResponse(),
        Ct = default,
    };

    private static async Task<List<SseItem<string>>> ForwardResponsesAndDrainAsync(
        string path, Stream upstream, int keepAliveSeconds)
    {
        var ctx = ResponsesCtx(path);
        var strategy = new CopilotBridge.Cli.Pipeline.Strategies.Codex.CopilotResponsesStrategy(
            new StubClient(StreamingResponse(upstream)),
            new CopilotBridge.Cli.Pipeline.Routing.CodexModelProfileCatalog(),
            ctx,
            TestAudit.Create(false),
            Options.Create(new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 30,
                KeepAliveIntervalSeconds = keepAliveSeconds,
            }),
            NullLogger<CopilotBridge.Cli.Pipeline.Strategies.Codex.CopilotResponsesStrategy>.Instance);
        await strategy.ForwardAsync();
        Assert.NotNull(ctx.Response.EventStream);
        var items = new List<SseItem<string>>();
        await foreach (var e in ctx.Response.EventStream!) items.Add(e);
        return items;
    }

    /// <summary>A Responses-shaped stream that opens an item, goes silent, then completes.</summary>
    private static SilentGapStream SilentResponsesStream(TimeSpan silence) => new(
        Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"type\":\"response.created\"}\n\n"),
        silence,
        Encoding.UTF8.GetBytes(
            "event: response.completed\ndata: {\"type\":\"response.completed\","
            + "\"response\":{\"status\":\"completed\"}}\n\n"));

    /// <summary>
    /// Contract: keepalive eligibility follows the DOWNSTREAM CLIENT PROTOCOL, not the
    /// upstream strategy. A `/cc` request routed to a Responses model (the CC→gpt
    /// route) still terminates at Claude Code, with the same idle watchdogs a native
    /// `/cc` stream faces — so it must get keepalives even though the Responses
    /// strategy served it. Scoping injection to the passthrough strategy would leave
    /// this supported route exposed.
    /// </summary>
    [Fact]
    public async Task ResponsesStrategy_CcRoute_InjectsKeepAlive()
    {
        var items = await ForwardResponsesAndDrainAsync(
            "/cc/v1/messages", SilentResponsesStream(TimeSpan.FromMilliseconds(2500)), keepAliveSeconds: 1);

        Assert.Contains(items, IsPing);
        Assert.All(items.Where(IsPing), p => Assert.Equal("{\"type\":\"ping\"}", p.Data));
    }

    /// <summary>
    /// Contract: a NATIVE Codex client gets no keepalive. Its stream is rendered back
    /// to the Responses protocol by T4, and what a Responses client accepts as progress
    /// has not been probed — so the bridge must not invent an event for it.
    /// </summary>
    [Fact]
    public async Task ResponsesStrategy_CodexRoute_InjectsNoKeepAlive()
    {
        var items = await ForwardResponsesAndDrainAsync(
            "/codex/responses", SilentResponsesStream(TimeSpan.FromMilliseconds(2500)), keepAliveSeconds: 1);

        Assert.DoesNotContain(items, IsPing);
    }

    /// <summary>
    /// Contract: an injected keepalive is identifiable as bridge-originated, and an
    /// event that merely looks like one is not. Identity-based marking means a ping
    /// Copilot itself sent would be reported honestly as upstream's, never claimed as
    /// the bridge's.
    /// </summary>
    [Fact]
    public void InjectedMarker_IdentifiesOnlyBridgeSynthesizedPings()
    {
        Assert.True(StreamKeepAlive.IsInjected(StreamKeepAlive.Ping()));
        // Same bytes, different origin (as an upstream-parsed event would be).
        var lookalike = new SseItem<string>(new string("{\"type\":\"ping\"}".ToCharArray()), "ping");
        Assert.False(StreamKeepAlive.IsInjected(lookalike));
    }
}

using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract coverage for the upstream inactivity timeout (Pipeline:UpstreamTimeout).
/// Each test states the required behaviour — a stalled upstream is bounded by the
/// bridge, a progressing one is never touched, and disabling the budget restores
/// the original path — and asserts it against the real client / strategy / endpoint.
/// The budgets are set to small sub-second values so the tests stay fast; the
/// contract is about the ABORT-on-idle vs PASS-on-progress decision, not the exact
/// wall-clock number.
/// </summary>
public class UpstreamTimeoutContractTests
{
    // ── Building blocks ───────────────────────────────────────────────────────

    private static UpstreamTimeoutOptions Timeouts(
        int firstByteSeconds, int streamIdleSeconds,
        UpstreamTimeoutAction action = UpstreamTimeoutAction.Retry) => new()
    {
        FirstByteTimeoutSeconds = firstByteSeconds,
        StreamIdleTimeoutSeconds = streamIdleSeconds,
        StreamIdleAction = action,
    };

    private static BridgeContext<MessagesRequest> Ctx(bool stream, CancellationToken ct = default) => new()
    {
        Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/cc/v1/messages",
            Body = new MessagesRequest
            {
                Model = "claude-opus-4-8",
                Messages = Array.Empty<MessageParam>(),
                Stream = stream,
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
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static CopilotMessagesPassthroughStrategy Strategy(
        ICopilotClient client, BridgeContext<MessagesRequest> ctx, UpstreamTimeoutOptions t) =>
        new(client, ctx, TestAudit.Create(false), Options.Create(t),
            NullLogger<CopilotMessagesPassthroughStrategy>.Instance);

    private static HttpResponseMessage StreamingResponse(Stream body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    private static byte[] OneEvent(string text) => Encoding.UTF8.GetBytes(
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}\n\n");

    private static async Task<List<SseItem<string>>> DrainAsync(IAsyncEnumerable<SseItem<string>> stream)
    {
        var items = new List<SseItem<string>>();
        await foreach (var e in stream) items.Add(e);
        return items;
    }

    // A stream that serves `prefix` bytes, then on the NEXT read blocks forever
    // honouring the cancellation token — i.e. an upstream that went silent
    // mid-stream. A cancellation-honouring block is what the idle CTS must
    // interrupt; a raw Task.Delay that ignored ct would not model the real read.
    private sealed class StallAfterPrefixStream(byte[] prefix) : Stream
    {
        private int _pos;
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
            // Prefix exhausted — block until cancelled (upstream went silent).
            await Task.Delay(Timeout.Infinite, ct);
            return 0; // unreachable
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    // ── Group 2.2: the exception is NOT classified transient ──────────────────

    /// <summary>
    /// Contract: an <see cref="UpstreamTimeoutException"/> is a bridge-initiated
    /// abort, not a network fault — so the transient classifier must reject it,
    /// which is what keeps the client retry loop from re-sending it and the
    /// endpoint's transient branch from mislabelling it a 502.
    /// </summary>
    [Fact]
    public void TransientClassifier_RejectsUpstreamTimeout()
    {
        var ex = new UpstreamTimeoutException(UpstreamTimeoutPhase.FirstByte, TimeSpan.FromSeconds(1));
        Assert.False(TransientUpstreamError.Is(ex));
    }

    // ── Group 6.1 / 6.4: first-byte timeout throws; stream-idle timeout throws ──

    /// <summary>
    /// Contract: when Copilot never returns response headers within the first-byte
    /// budget, PostMessagesAsync aborts the send with a FirstByte timeout. Uses a
    /// real HttpClient over a handler that never completes until cancelled.
    /// </summary>
    [Fact]
    public async Task FirstByte_NeverArrives_ThrowsFirstByteTimeout()
    {
        using var http = new HttpClient(new NeverRespondsHandler());
        var client = new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 0 }),
            Options.Create(Timeouts(firstByteSeconds: 1, streamIdleSeconds: 0)),
            NullLogger<CopilotClient>.Instance);

        var ex = await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
            await client.PostMessagesAsync(SomeBody()));
        Assert.Equal(UpstreamTimeoutPhase.FirstByte, ex.Phase);
    }

    /// <summary>
    /// Mutation guard for the above: with the first-byte budget DISABLED the send
    /// is NOT aborted by our timer. We prove this by cancelling via the caller's
    /// own token instead and asserting the surfaced cancellation is the client's
    /// (a plain OperationCanceledException), never an UpstreamTimeoutException.
    /// </summary>
    [Fact]
    public async Task FirstByte_Disabled_NoTimeoutThrown()
    {
        using var http = new HttpClient(new NeverRespondsHandler());
        var client = new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 0 }),
            Options.Create(Timeouts(firstByteSeconds: 0, streamIdleSeconds: 0)),
            NullLogger<CopilotClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.PostMessagesAsync(SomeBody(), ct: cts.Token));
        // The disabled-budget path must not have converted the client cancel into
        // a timeout — assert the concrete type is NOT our exception.
        // (ThrowsAnyAsync<OperationCanceledException> already excludes it, since
        // UpstreamTimeoutException is a plain Exception, not an OCE.)
    }

    /// <summary>
    /// Contract (6.2): if headers arrive before the first-byte budget elapses,
    /// however close, the send succeeds — the budget bounds inactivity, not a slow
    /// success. Handler responds after ~150ms; budget is a comfortable 5s.
    /// </summary>
    [Fact]
    public async Task FirstByte_ArrivesBeforeBudget_Succeeds()
    {
        using var http = new HttpClient(new DelayedHandler(TimeSpan.FromMilliseconds(150)));
        var client = new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 0 }),
            Options.Create(Timeouts(firstByteSeconds: 5, streamIdleSeconds: 0)),
            NullLogger<CopilotClient>.Instance);

        using var resp = await client.PostMessagesAsync(SomeBody());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    /// Contract (6.3): retry backoff must NOT be counted against the first-byte
    /// budget — each fresh send gets the full budget. One transient failure forces
    /// a backoff, then a send that responds within the (per-attempt) budget must
    /// succeed. If the budget spanned all attempts + backoff it would be exhausted;
    /// per-attempt arming keeps it whole. Budget 2s, backoff ~small, response fast.
    /// </summary>
    [Fact]
    public async Task FirstByte_Backoff_DoesNotConsumeBudget()
    {
        // Attempt 1: a transient failure (retryable). Attempt 2: a fast 200.
        var handler = new ScriptedThrowThenOk(
            firstError: new HttpRequestException("connection reset"),
            delayBeforeOk: TimeSpan.FromMilliseconds(50));
        using var http = new HttpClient(handler);
        var client = new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 2, BaseDelayMs = 100, BackoffMultiplier = 1.0, MaxDelayMs = 100 }),
            Options.Create(Timeouts(firstByteSeconds: 2, streamIdleSeconds: 0)),
            NullLogger<CopilotClient>.Instance);

        using var resp = await client.PostMessagesAsync(SomeBody());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    /// <summary>
    /// Contract (6.8 / design D5): when the client cancels while the first byte is
    /// still pending, the surfaced error is the client's cancellation, NOT an
    /// upstream timeout — client-cancel wins the race. The throw site guards on
    /// <c>!ct.IsCancellationRequested</c>, so a cancelled caller token never
    /// converts to <see cref="UpstreamTimeoutException"/>.
    /// </summary>
    [Fact]
    public async Task ClientCancel_WhileFirstBytePending_ReportsCancel_NotTimeout()
    {
        using var http = new HttpClient(new NeverRespondsHandler());
        var client = new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 0 }),
            // A long budget so the timer does NOT fire first; the client cancels.
            Options.Create(Timeouts(firstByteSeconds: 30, streamIdleSeconds: 0)),
            NullLogger<CopilotClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.PostMessagesAsync(SomeBody(), ct: cts.Token));
        Assert.IsNotType<UpstreamTimeoutException>(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>
    /// Contract: once the stream has started, if Copilot goes silent for longer
    /// than the stream-idle budget the strategy aborts the read with a StreamIdle
    /// timeout — even though a first, complete event was already delivered.
    /// </summary>
    [Fact]
    public async Task StreamIdle_UpstreamGoesSilent_ThrowsStreamIdleTimeout()
    {
        var ctx = Ctx(stream: true);
        var body = new StallAfterPrefixStream(OneEvent("hi"));
        var strategy = Strategy(new StubClient(StreamingResponse(body)), ctx,
            Timeouts(firstByteSeconds: 0, streamIdleSeconds: 1));

        await strategy.ForwardAsync();

        var ex = await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
            await DrainAsync(ctx.Response.EventStream!));
        Assert.Equal(UpstreamTimeoutPhase.StreamIdle, ex.Phase);
    }

    /// <summary>
    /// Mutation guard: with the stream-idle budget DISABLED the same silent
    /// upstream is NOT aborted by the bridge — the read blocks on the caller's
    /// token instead. Cancelling that token surfaces a plain cancellation, never
    /// an UpstreamTimeoutException.
    /// </summary>
    [Fact]
    public async Task StreamIdle_Disabled_NoTimeoutThrown()
    {
        using var cts = new CancellationTokenSource();
        var ctx = Ctx(stream: true, ct: cts.Token);
        var body = new StallAfterPrefixStream(OneEvent("hi"));
        var strategy = Strategy(new StubClient(StreamingResponse(body)), ctx,
            Timeouts(firstByteSeconds: 0, streamIdleSeconds: 0));

        await strategy.ForwardAsync();

        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DrainAsync(ctx.Response.EventStream!));
    }

    /// <summary>
    /// Contract (D5, stream-idle site): with the budget ENABLED but long (30s), a
    /// client cancel mid-stall wins the race — the surface is a plain
    /// `OperationCanceledException`, NOT an `UpstreamTimeoutException`. This exercises
    /// the `&amp;&amp; !ct.IsCancellationRequested` guard at the stream-idle throw site
    /// (the disabled test above arms no timer, so it can't prove the guard). Dropping
    /// that guard clause would mislabel this client cancel as a stream_idle timeout.
    /// </summary>
    [Fact]
    public async Task StreamIdle_ClientCancelWithArmedBudget_ReportsCancel_NotTimeout()
    {
        using var cts = new CancellationTokenSource();
        var ctx = Ctx(stream: true, ct: cts.Token);
        var body = new StallAfterPrefixStream(OneEvent("hi"));
        var strategy = Strategy(new StubClient(StreamingResponse(body)), ctx,
            Timeouts(firstByteSeconds: 0, streamIdleSeconds: 30)); // armed, but won't fire first

        await strategy.ForwardAsync();

        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await DrainAsync(ctx.Response.EventStream!));
        Assert.IsNotType<UpstreamTimeoutException>(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }


    /// <summary>
    /// Contract: a stream whose every inter-event gap is under the budget is never
    /// aborted, and every event is relayed. Uses a paced source that emits three
    /// complete events well within a generous budget.
    /// </summary>
    [Fact]
    public async Task StreamIdle_KeepsEmitting_AllEventsRelayed()
    {
        var sse = new MemoryStream(Concat(OneEvent("a"), OneEvent("b"), OneEvent("c"),
            Encoding.UTF8.GetBytes("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n")));
        var ctx = Ctx(stream: true);
        var strategy = Strategy(new StubClient(StreamingResponse(sse)), ctx,
            Timeouts(firstByteSeconds: 0, streamIdleSeconds: 30));

        await strategy.ForwardAsync();
        var got = await DrainAsync(ctx.Response.EventStream!);

        // 3 deltas + message_stop, none dropped.
        Assert.Equal(4, got.Count);
    }

    /// <summary>
    /// Race guard (Copilot round-2 finding): several events each delivered after a
    /// real wait that is a large fraction of the budget, so the budget is genuinely
    /// armed and reset per event. The old arm/disarm-on-a-reused-CTS shape could
    /// poison the source if a timer fired between a successful read and the disarm,
    /// spuriously aborting the NEXT read; the WhenAny-race mechanism cannot. With
    /// per-event gaps well under budget, all events must relay and no timeout fires.
    /// </summary>
    [Fact]
    public async Task StreamIdle_PacedUnderBudget_ManyEvents_NeverSpuriouslyAborts()
    {
        // 5 events, each after a ~120ms wait, against a 1s budget (each gap is 12%
        // of budget — genuinely armed, never exceeded). A cumulative-poisoning bug
        // would trip on event 2+.
        var events = new[] { "a", "b", "c", "d", "e" }.Select(OneEvent).ToArray();
        var ctx = Ctx(stream: true);
        var strategy = Strategy(
            new StubClient(StreamingResponse(new PacedStream(events, TimeSpan.FromMilliseconds(120)))),
            ctx, Timeouts(firstByteSeconds: 0, streamIdleSeconds: 1));

        await strategy.ForwardAsync();
        var got = await DrainAsync(ctx.Response.EventStream!);

        Assert.Equal(events.Length, got.Count); // every paced event relayed, none aborted
    }

    // A stream that delivers each chunk only after `gap` elapses (honouring ct), so
    // consecutive events are separated by a real wait — exercising the armed-budget
    // path per event, not a synchronous burst from a MemoryStream.
    private sealed class PacedStream(byte[][] chunks, TimeSpan gap) : Stream
    {
        private int _chunk;
        private int _within;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_chunk >= chunks.Length) return 0; // end of stream
            if (_within == 0) await Task.Delay(gap, ct); // wait before each new chunk
            var cur = chunks[_chunk];
            var n = Math.Min(buffer.Length, cur.Length - _within);
            cur.AsSpan(_within, n).CopyTo(buffer.Span);
            _within += n;
            if (_within >= cur.Length) { _chunk++; _within = 0; }
            return n;
        }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    // ── Group 6.7: disabled budgets ⇒ byte-identical to the no-timeout path ────

    /// <summary>
    /// Contract: with both budgets disabled the streaming relay is byte-identical
    /// to a straight SSE parse — the timeout feature adds nothing to the wire when
    /// off. Asserts the full event sequence equals a no-wrapper baseline.
    /// </summary>
    [Fact]
    public async Task Disabled_StreamingIsByteIdenticalToNoTimeoutPath()
    {
        var raw = Concat(OneEvent("x"), OneEvent("y"),
            Encoding.UTF8.GetBytes("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"));
        var baseline = await DrainAsync(SseParser.Create(new MemoryStream(raw)).EnumerateAsync());

        var ctx = Ctx(stream: true);
        var strategy = Strategy(new StubClient(StreamingResponse(new MemoryStream(raw))), ctx,
            Timeouts(firstByteSeconds: 0, streamIdleSeconds: 0));
        await strategy.ForwardAsync();
        var got = await DrainAsync(ctx.Response.EventStream!);

        Assert.Equal(baseline.Count, got.Count);
        for (var i = 0; i < baseline.Count; i++)
        {
            Assert.Equal(baseline[i].EventType, got[i].EventType);
            Assert.Equal(baseline[i].Data, got[i].Data);
        }
    }

    // ── Group 6.1 (endpoint): first-byte timeout ⇒ 504 + summary field ─────────

    /// <summary>
    /// Contract: a first-byte timeout that reaches the endpoint (no bytes sent yet)
    /// surfaces as a real 504 to the client and records upstream_timeout=first_byte
    /// on the summary — distinct from a client cancel.
    /// </summary>
    [Fact]
    public async Task Endpoint_FirstByteTimeout_Returns504()
    {
        var (summary, statusCode, responseBody) = await RunEndpoint(
            requestJson: """{"model":"claude-opus-4-8","max_tokens":16,"stream":true,"messages":[{"role":"user","content":"x"}]}""",
            strategyBehavior: StrategyBehavior.ThrowFirstByteBeforeHeaders,
            action: UpstreamTimeoutAction.Retry);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, statusCode);
        Assert.Equal("first_byte", summary);
        Assert.Contains("first byte", responseBody);
    }

    /// <summary>
    /// Contract: a mid-stream timeout with the default Retry action injects the
    /// retryable overloaded_error event (byte-identical to a guard trip) after the
    /// already-delivered events; the wire status stays 200 and the summary records
    /// upstream_timeout=stream_idle.
    /// </summary>
    [Fact]
    public async Task Endpoint_MidStreamTimeout_Retry_InjectsErrorEvent()
    {
        var (summary, statusCode, responseBody) = await RunEndpoint(
            requestJson: """{"model":"claude-opus-4-8","max_tokens":16,"stream":true,"messages":[{"role":"user","content":"x"}]}""",
            strategyBehavior: StrategyBehavior.EmitOneThenThrowStreamIdle,
            action: UpstreamTimeoutAction.Retry);

        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal("stream_idle", summary);
        // Guard-parity, exact enough to catch a real regression without freezing the
        // private message string: the injected event is in SSE `error` framing with
        // the shared retryable envelope shape (type=error, error.type=overloaded_error,
        // a bridge-authored message), appearing EXACTLY ONCE and AFTER the relayed
        // first event. Substring checks alone would pass on a duplicated event, on the
        // error emitted BEFORE the text, or on regressed framing — so assert order +
        // count + the exact envelope prefix.
        var errorIdx = responseBody.IndexOf("event: error", StringComparison.Ordinal);
        var deltaIdx = responseBody.IndexOf("text_delta", StringComparison.Ordinal);
        Assert.True(deltaIdx >= 0, "the first (text_delta) event must have been relayed");
        Assert.True(errorIdx > deltaIdx, "the error event must come AFTER the relayed first event");
        // Exactly one injected error event (no duplication).
        Assert.Equal(errorIdx, responseBody.LastIndexOf("event: error", StringComparison.Ordinal));
        // Exact shared-envelope prefix (the message text itself is an impl detail).
        Assert.Contains(
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"[copilot-bridge]",
            responseBody);
    }

    /// <summary>
    /// Contract: a mid-stream timeout with Truncate ends the stream with NO error
    /// event, but still records the timeout on the summary.
    /// </summary>
    [Fact]
    public async Task Endpoint_MidStreamTimeout_Truncate_NoErrorEvent()
    {
        var (summary, statusCode, responseBody) = await RunEndpoint(
            requestJson: """{"model":"claude-opus-4-8","max_tokens":16,"stream":true,"messages":[{"role":"user","content":"x"}]}""",
            strategyBehavior: StrategyBehavior.EmitOneThenThrowStreamIdle,
            action: UpstreamTimeoutAction.Truncate);

        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal("stream_idle", summary);
        Assert.DoesNotContain("overloaded_error", responseBody);
        // Truncation must still have RELAYED the events delivered before the stall —
        // a regression that dropped all prior bytes would also pass the no-error
        // check above, so pin the survival of the first event.
        Assert.Contains("text_delta", responseBody);
    }

    // ── Endpoint harness ──────────────────────────────────────────────────────

    private enum StrategyBehavior { ThrowFirstByteBeforeHeaders, EmitOneThenThrowStreamIdle }

    // A strategy stand-in that populates ctx.Response like the real one, then
    // throws an UpstreamTimeoutException at the point the real code would: before
    // any events (first-byte), or from inside the event stream after one event
    // (stream-idle). This isolates the endpoint mapping from the read machinery.
    private sealed class FakeTimeoutStrategy(
        BridgeContext<MessagesRequest> ctx, StrategyBehavior behavior) : IUpstreamStrategy<MessagesRequest>
    {
        public string Name => "FakeTimeout";
        public bool Matches(RouteTarget target) => true;

        public Task ForwardAsync()
        {
            if (behavior == StrategyBehavior.ThrowFirstByteBeforeHeaders)
            {
                throw new UpstreamTimeoutException(UpstreamTimeoutPhase.FirstByte, TimeSpan.FromSeconds(1));
            }
            ctx.Response.Status = 200;
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Mode = ResponseMode.Streaming;
            ctx.Response.EventStream = OneEventThenThrow();
            return Task.CompletedTask;
        }

        private static async IAsyncEnumerable<SseItem<string>> OneEventThenThrow()
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}",
                "content_block_delta");
            await Task.Yield();
            throw new UpstreamTimeoutException(UpstreamTimeoutPhase.StreamIdle, TimeSpan.FromSeconds(1));
        }
    }

    private sealed class Runner(
        BridgeContext<MessagesRequest> ctx, IUpstreamStrategy<MessagesRequest> strategy)
        : IPipelineRunner<MessagesRequest>
    {
        public async Task RunAsync(Pipeline<MessagesRequest> pipeline)
        {
            ctx.OriginalRequestedModel = ctx.Request.Body.Model;
            ctx.Target = new RouteTarget(BackendVendor.CopilotAnthropic, "/v1/messages", ctx.Request.Body.Model);
            await strategy.ForwardAsync();
        }
    }

    private static async Task<(string Summary, int StatusCode, string Body)> RunEndpoint(
        string requestJson, StrategyBehavior strategyBehavior, UpstreamTimeoutAction action)
    {
        var bridgeCtx = new BridgeContext<MessagesRequest>();
        var strategy = new FakeTimeoutStrategy(bridgeCtx, strategyBehavior);

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var respStream = new MemoryStream();
        // Model Kestrel's HasStarted semantics: it flips true once response bytes
        // are first written/flushed. DefaultHttpContext with a plain MemoryStream
        // never flips it, which would make a mid-stream write look pre-headers and
        // mis-route the timeout to the 504 branch. This feature flips HasStarted on
        // the first body write, so the endpoint's HasStarted check is realistic.
        var respFeature = new StartTrackingResponseFeature();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(respFeature);
        http.Response.Body = new StartTrackingStream(respStream, respFeature);

        var summaryCapture = new SummaryCapturingLogger();

        await ClaudeCodeMessagesEndpoint.HandleAsync(
            http,
            bridgeCtx,
            new Runner(bridgeCtx, strategy),
            new Pipeline<MessagesRequest>
            {
                Name = "test",
                RequestStages = [],
                ResponseStages = [],
                Strategies = new StrategyRegistry<MessagesRequest>([]),
            },
            new ClaudeCodeInboundAdapter(NullLogger<ClaudeCodeInboundAdapter>.Instance),
            new ClaudeCodeOutboundAdapter(NullLogger<ClaudeCodeOutboundAdapter>.Instance),
            new ModelProfileCatalog(),
            summaryCapture.Logger,
            TestAudit.Create(false),
            Options.Create(new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 0,
                StreamIdleAction = action,
            }),
            NullLogger<ClaudeCodeMessagesEndpointTag>.Instance);

        var statusCode = http.Response.StatusCode;
        var responseBody = Encoding.UTF8.GetString(respStream.ToArray());
        return (summaryCapture.UpstreamTimeout ?? "(none)", statusCode, responseBody);
    }

    // Captures the RequestSummary the endpoint logs so tests can assert the
    // upstream_timeout field without parsing the rendered line.
    private sealed class SummaryCapturingLogger
    {
        public string? UpstreamTimeout { get; private set; }
        public RequestSummaryLogger Logger { get; }

        public SummaryCapturingLogger()
        {
            Logger = new RequestSummaryLogger(new Interceptor(this));
        }

        private sealed class Interceptor(SummaryCapturingLogger owner)
            : Microsoft.Extensions.Logging.ILogger<RequestSummaryLogger>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // The summary logger renders named placeholders; find UpstreamTimeout
                // from the structured key/value pairs.
                if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
                {
                    foreach (var kv in kvs)
                    {
                        if (kv.Key == "UpstreamTimeout")
                        {
                            var v = kv.Value?.ToString();
                            owner.UpstreamTimeout = v == "(none)" ? null : v;
                        }
                    }
                }
            }
        }
    }

    // A minimal IHttpResponseFeature that reports HasStarted=true once flagged,
    // so the endpoint's HasStarted check behaves like Kestrel (which flips it on
    // first body write). Status code + headers are backed here so DefaultHttpContext
    // reads them consistently.
    private sealed class StartTrackingResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public Microsoft.AspNetCore.Http.IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }
        public void MarkStarted() => HasStarted = true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    // Wraps the real capture stream; flips the response feature's HasStarted on the
    // first write, mirroring Kestrel.
    private sealed class StartTrackingStream(Stream inner, StartTrackingResponseFeature feature) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c)
        {
            feature.MarkStarted();
            inner.Write(b, o, c);
        }
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct)
        {
            feature.MarkStarted();
            return inner.WriteAsync(b, o, c, ct);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
        {
            feature.MarkStarted();
            return inner.WriteAsync(b, ct);
        }
    }

    // ── small helpers ─────────────────────────────────────────────────────────

    private static ReadOnlyMemory<byte> SomeBody() =>
        Encoding.UTF8.GetBytes("""{"model":"claude-opus-4.8","messages":[]}""");

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var buf = new byte[total];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(buf, o); o += p.Length; }
        return buf;
    }

    // A handler that never returns until its token is cancelled — models an
    // upstream that accepts the connection but never sends response headers.
    private sealed class NeverRespondsHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable
        }
    }

    // Responds 200 after a fixed delay (honouring cancellation) — a slow-but-real
    // first byte.
    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            };
        }
    }

    // First call throws a transient error (drives one retry); second call returns
    // 200 after a short delay. Proves the per-attempt budget is not consumed by the
    // inter-attempt backoff.
    private sealed class ScriptedThrowThenOk(Exception firstError, TimeSpan delayBeforeOk) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1) throw firstError;
            await Task.Delay(delayBeforeOk, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }

    // Minimal IAuthService fake (base URL + token), mirroring CopilotClientRetryTests.
    private sealed class FakeAuth : CopilotBridge.Cli.Auth.IAuthService
    {
        public bool IsAuthenticated => true;
        public string TokenLocation => "(test)";
        public string? CopilotApiBaseUrl => "https://api.test.githubcopilot.com";
        public DateTimeOffset? CopilotTokenExpiry => DateTimeOffset.MaxValue;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult("gh-token");
        public ValueTask<string> GetCopilotTokenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult("test-token");
        public void SignOut() { }
    }
}

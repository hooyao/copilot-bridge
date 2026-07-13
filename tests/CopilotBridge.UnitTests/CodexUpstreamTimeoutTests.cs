using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract coverage for the upstream inactivity timeout on the Codex/Responses
/// path. The upstream strategy propagates a mid-stream fault through the IR; the
/// Codex T4 boundary emits `response.failed` and rethrows it for endpoint
/// accounting. The first-byte budget is
/// shared with `/cc` (both use `CopilotClient.SendWithFirstByteBudgetAsync`), so
/// first-byte behavior is covered by both this file and the `/cc` file.
/// </summary>
public class CodexUpstreamTimeoutTests
{
    private static UpstreamTimeoutOptions Timeouts(int firstByte, int streamIdle) => new()
    {
        FirstByteTimeoutSeconds = firstByte,
        StreamIdleTimeoutSeconds = streamIdle,
    };

    private static BridgeContext<MessagesRequest> Ctx(bool stream, CancellationToken ct = default) => new()
    {
        Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/codex/responses",
            Body = new MessagesRequest
            {
                Model = "gpt-5.3-codex",
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
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) => new(resp);
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static CopilotResponsesStrategy Strategy(
        ICopilotClient client, BridgeContext<MessagesRequest> ctx, UpstreamTimeoutOptions t) =>
        new(client, new CodexModelProfileCatalog(), ctx, TestAudit.Create(false), Options.Create(t),
            NullLogger<CopilotResponsesStrategy>.Instance);

    private static HttpResponseMessage StreamingResponse(Stream body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    // A well-formed Responses "start" event, so the state machine is past
    // response.created when the stall hits.
    private static byte[] ResponsesPrefix() => Encoding.UTF8.GetBytes(
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}\n\n");

    private static async Task<List<SseItem<string>>> DrainAsync(IAsyncEnumerable<SseItem<string>> stream)
    {
        var items = new List<SseItem<string>>();
        await foreach (var e in stream) items.Add(e);
        return items;
    }

    // Serves `prefix`, then blocks (honouring the token) — upstream went silent.
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
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] b, int o, int c) =>
            ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>
    /// Contract: a Responses strategy is upstream-facing and therefore cannot
    /// choose a Codex or Claude failure envelope. A mid-stream stall propagates an
    /// UpstreamTimeoutException(StreamIdle) after the partial IR, with no private
    /// failure terminal appended.
    /// </summary>
    [Fact]
    public async Task ResponsesStrategy_MidStreamStall_PropagatesTimeout_NoPrivateTerminal()
    {
        var ctx = Ctx(stream: true);
        var body = new StallAfterPrefixStream(ResponsesPrefix());
        var strategy = Strategy(new StubClient(StreamingResponse(body)), ctx, Timeouts(0, streamIdle: 1));

        await strategy.ForwardAsync();
        var got = new List<SseItem<string>>();
        var ex = await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
        {
            await foreach (var item in ctx.Response.EventStream!) got.Add(item);
        });

        Assert.Equal(UpstreamTimeoutPhase.StreamIdle, ex.Phase);
        Assert.NotEmpty(got);
        Assert.DoesNotContain(got, item => item.Data.Contains("\"stop_reason\":\"error\""));
        Assert.DoesNotContain(got, item => item.EventType == "message_stop");
    }

    /// <summary>
    /// Contract: the Codex edge, not T3, catches the same propagated fault. T4
    /// writes one response.failed and no Anthropic error before rethrowing.
    /// </summary>
    [Fact]
    public async Task Codex_MidStreamStall_T4EmitsOneFailedThenRethrows()
    {
        var ctx = Ctx(stream: true);
        var body = new StallAfterPrefixStream(ResponsesPrefix());
        var strategy = Strategy(new StubClient(StreamingResponse(body)), ctx, Timeouts(0, streamIdle: 1));

        await strategy.ForwardAsync();
        var adapter = new IrToResponsesOutboundAdapter(
            ctx, NullLogger<IrToResponsesOutboundAdapter>.Instance);
        var got = new List<SseItem<string>>();
        await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
        {
            await foreach (var item in adapter.AdaptStreamAsync(ctx.Response.EventStream!, default))
                got.Add(item);
        });

        Assert.Single(got, item => item.EventType == "response.failed");
        Assert.DoesNotContain(got, item => item.Data.Contains("overloaded_error"));
    }

    /// <summary>
    /// Mutation guard: with the stream-idle budget disabled the same silent upstream
    /// is NOT aborted by the bridge — the read blocks on the caller's token. Cancel
    /// it and the surfaced fault is a plain cancellation, not our timeout.
    /// </summary>
    [Fact]
    public async Task Codex_MidStreamStall_Disabled_NoTimeoutFault()
    {
        using var cts = new CancellationTokenSource();
        var ctx = Ctx(stream: true, ct: cts.Token);
        var body = new StallAfterPrefixStream(ResponsesPrefix());
        var strategy = Strategy(new StubClient(StreamingResponse(body)), ctx, Timeouts(0, streamIdle: 0));

        await strategy.ForwardAsync();
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DrainAsync(ctx.Response.EventStream!));
    }

    /// <summary>
    /// Contract (D5, Codex stream-idle site): with the budget ENABLED but long
    /// (30s), a client cancel mid-stall wins the race — the latched
    /// thrown exception remains a client cancellation, not an upstream timeout.
    /// Exercises the
    /// `&amp;&amp; !ct.IsCancellationRequested` guard at the Codex throw site, which the
    /// disabled test cannot (it arms no timer).
    /// </summary>
    [Fact]
    public async Task Codex_ClientCancelWithArmedBudget_FaultIsNotTimeout()
    {
        using var cts = new CancellationTokenSource();
        var ctx = Ctx(stream: true, ct: cts.Token);
        var body = new StallAfterPrefixStream(ResponsesPrefix());
        var strategy = Strategy(new StubClient(StreamingResponse(body)), ctx, Timeouts(0, streamIdle: 30));

        await strategy.ForwardAsync();
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DrainAsync(ctx.Response.EventStream!));
    }

    /// <summary>
    /// Contract: a Codex stream that keeps emitting within the budget is never
    /// aborted and surfaces no fault. Two complete events then a clean completed.
    /// </summary>
    [Fact]
    public async Task Codex_KeepsEmitting_NoFault()
    {
        var sse = Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}\n\n"
            + "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\"}}\n\n");
        var ctx = Ctx(stream: true);
        var strategy = Strategy(new StubClient(StreamingResponse(new MemoryStream(sse))), ctx, Timeouts(0, streamIdle: 30));

        await strategy.ForwardAsync();
        await DrainAsync(ctx.Response.EventStream!);

    }

    /// <summary>
    /// Contract: with the stream-idle budget DISABLED the Codex T3 translation is
    /// byte-identical to the enabled-but-responsive path — the timer wrapper adds
    /// nothing to the wire. Runs the same clean source through budget=0 and a large
    /// (non-firing) budget and asserts the drained T3 event sequences are equal.
    /// Mirrors the `/cc` byte-identity test (which the Codex suite otherwise lacked).
    /// </summary>
    [Fact]
    public async Task Codex_Disabled_T3StreamByteIdenticalToResponsivePath()
    {
        byte[] Sse() => Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}\n\n"
            + "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}\n\n"
            + "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\"}}\n\n");

        var ctxOff = Ctx(stream: true);
        var off = Strategy(new StubClient(StreamingResponse(new MemoryStream(Sse()))), ctxOff, Timeouts(0, streamIdle: 0));
        await off.ForwardAsync();
        var seqOff = await DrainAsync(ctxOff.Response.EventStream!);

        var ctxOn = Ctx(stream: true);
        var on = Strategy(new StubClient(StreamingResponse(new MemoryStream(Sse()))), ctxOn, Timeouts(0, streamIdle: 30));
        await on.ForwardAsync();
        var seqOn = await DrainAsync(ctxOn.Response.EventStream!);

        Assert.Equal(seqOff.Count, seqOn.Count);
        for (var i = 0; i < seqOff.Count; i++)
        {
            Assert.Equal(seqOff[i].EventType, seqOn[i].EventType);
            Assert.Equal(seqOff[i].Data, seqOn[i].Data);
        }
    }

    /// <summary>
    /// Contract: the Codex first-byte budget is driven through `PostResponsesAsync`
    /// directly (not just the shared helper via `PostMessagesAsync`) — that method
    /// has its OWN retry loop + `when` clause, so a regression where it forgot to
    /// call the helper, or whose `when` swallowed the timeout, must fail here. With
    /// no headers ever arriving, `PostResponsesAsync` throws
    /// `UpstreamTimeoutException(FirstByte)`.
    /// </summary>
    [Fact]
    public async Task Codex_FirstByte_NeverArrives_ThrowsFirstByteTimeout()
    {
        using var http = new HttpClient(new NeverRespondsHandler());
        var client = new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 0 }),
            Options.Create(Timeouts(firstByte: 1, streamIdle: 0)),
            NullLogger<CopilotClient>.Instance);

        var ex = await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
            await client.PostResponsesAsync(SomeBody()));
        Assert.Equal(UpstreamTimeoutPhase.FirstByte, ex.Phase);
    }

    /// <summary>
    /// Contract: the Codex first-byte timeout is TERMINAL — the transient-retry loop
    /// in `PostResponsesAsync` must not re-send it. With MaxRetries=2 and a handler
    /// that never responds, the handler is hit exactly once (no re-send).
    /// </summary>
    [Fact]
    public async Task Codex_FirstByte_Timeout_NotRetried()
    {
        var handler = new NeverRespondsHandler();
        using var http = new HttpClient(handler);
        var client = new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 2, BaseDelayMs = 1, BackoffMultiplier = 1.0, MaxDelayMs = 2 }),
            Options.Create(Timeouts(firstByte: 1, streamIdle: 0)),
            NullLogger<CopilotClient>.Instance);

        await Assert.ThrowsAsync<UpstreamTimeoutException>(async () =>
            await client.PostResponsesAsync(SomeBody()));
        Assert.Equal(1, handler.CallCount);
    }

    private static ReadOnlyMemory<byte> SomeBody() =>
        Encoding.UTF8.GetBytes("""{"model":"gpt-5.3-codex","input":[]}""");

    // Never returns until cancelled — models an upstream that accepts the connection
    // but never sends response headers.
    private sealed class NeverRespondsHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable
        }
    }

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

using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Case-driven coverage of the upstream-resp raw-capture contract. The trace
/// system promises that <c>upstream-resp</c> holds EXACTLY what Copilot returned
/// on the wire, before any pipeline stage, and that capturing this costs nothing
/// when tracing is off. These tests assert that promise end-to-end through a
/// strategy + a stubbed Copilot client — they describe required behaviour, not
/// the current implementation, so a refactor that breaks the promise fails here.
/// </summary>
public class UpstreamResponseCaptureContractTests
{
    // ── Copilot client stub: returns a caller-supplied HttpResponseMessage ────

    private sealed class StubClient(HttpResponseMessage resp) : ICopilotClient
    {
        public ValueTask<HttpResponseMessage> PostMessagesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            IReadOnlyList<string>? anthropicBeta = null,
            IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
            CancellationToken ct = default) => new(resp);

        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            CancellationToken ct = default) => new(resp);

        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static HttpResponseMessage StreamingResponse(byte[] sseBytes)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(sseBytes)),
        };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    private static HttpResponseMessage BufferedResponse(byte[] body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return resp;
    }

    private static BridgeContext<MessagesRequest> Ctx(string model, bool stream) => new()
    {
        Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/cc/v1/messages",
            Body = new MessagesRequest
            {
                Model = model,
                Messages = Array.Empty<MessageParam>(),
                Stream = stream,
            },
        },
        Response = new BridgeResponse(),
        Ct = default,
    };

    private static async Task<List<SseItem<string>>> DrainAsync(
        IAsyncEnumerable<SseItem<string>> stream)
    {
        var items = new List<SseItem<string>>();
        await foreach (var e in stream) items.Add(e);
        return items;
    }

    // A small but realistic Anthropic SSE body with a multi-byte UTF-8 payload.
    private static byte[] SampleSse()
    {
        var sb = new StringBuilder();
        sb.Append("event: message_start\n");
        sb.Append("data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-4.8\"}}\n\n");
        sb.Append("event: content_block_delta\n");
        sb.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"现在回到 D2\"}}\n\n");
        sb.Append("event: message_stop\n");
        sb.Append("data: {\"type\":\"message_stop\"}\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static CopilotMessagesPassthroughStrategy CcStrategy(ICopilotClient client, bool tracing, BridgeContext<MessagesRequest> ctx) =>
        new(client, ctx, TestAudit.Create(tracing),
            // Timeouts disabled: these capture/byte-identity tests must exercise the
            // original no-timer stream path, unperturbed by an inactivity budget.
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotMessagesPassthroughStrategy>.Instance);

    // ── Case A: tracing ON, streaming → capture == Copilot's raw wire bytes ───

    /// <summary>
    /// Contract: with tracing enabled, after the client has consumed a streaming
    /// response, the bridge must hold a byte-for-byte copy of the exact SSE bytes
    /// Copilot sent — that is the whole point of the upstream-resp artifact.
    /// </summary>
    [Fact]
    public async Task TracingOn_Streaming_CaptureEqualsRawCopilotBytes()
    {
        var raw = SampleSse();
        var ctx = Ctx("claude-opus-4-8", stream: true);
        var strategy = CcStrategy(new StubClient(StreamingResponse(raw)), tracing: true, ctx: ctx);

        await strategy.ForwardAsync();
        // The capture is filled only as the stream is consumed — drain it the way
        // the endpoint relay loop would.
        await DrainAsync(ctx.Response.EventStream!);

        Assert.NotNull(ctx.Response.RawUpstreamResponseCapture);
        Assert.Equal(raw, ctx.Response.RawUpstreamResponseCapture!.ToArray());
    }

    // ── Case B: tracing OFF → no capture, but client still gets every event ───

    /// <summary>
    /// Contract: with tracing disabled there must be NO raw capture (zero extra
    /// buffering — the user's explicit requirement), AND the client must still
    /// receive the identical event stream. Captured bytes are a debug-only cost.
    /// </summary>
    [Fact]
    public async Task TracingOff_Streaming_NoCapture_ButEventsUnchanged()
    {
        var raw = SampleSse();
        // Baseline: the events as parsed straight from Copilot's bytes.
        var expected = await DrainAsync(SseParser.Create(new MemoryStream(raw)).EnumerateAsync());

        var ctx = Ctx("claude-opus-4-8", stream: true);
        var strategy = CcStrategy(new StubClient(StreamingResponse(raw)), tracing: false, ctx: ctx);

        await strategy.ForwardAsync();
        var got = await DrainAsync(ctx.Response.EventStream!);

        Assert.Null(ctx.Response.RawUpstreamResponseCapture);
        Assert.Equal(expected.Count, got.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].EventType, got[i].EventType);
            Assert.Equal(expected[i].Data, got[i].Data);
        }
    }

    // ── Case C: buffered raw body survives a downstream model rewrite ─────────

    /// <summary>
    /// Contract: on the buffered path the captured upstream body must be Copilot's
    /// ORIGINAL bytes even after <see cref="ResponseModelRewriteStage"/> rewrites
    /// the model the client sees. So: run the real rewrite stage, then assert the
    /// captured body still names Copilot's model while the client-facing body
    /// names the requested one.
    /// </summary>
    [Fact]
    public async Task TracingOn_Buffered_CaptureKeepsCopilotModel_AfterRewrite()
    {
        // Copilot answered as its back-end variant; the client asked for opus-4-8.
        var copilotBody = Encoding.UTF8.GetBytes("""{"model":"claude-opus-4.7-1m-internal","type":"message"}""");

        // Realistic routing state: the router already rewrote Body.Model to the
        // resolved back-end variant and recorded the original the client asked for.
        // ResponseModelRewriteStage only fires when resolved != original, so the
        // resolved model must be the variant here — not the requested name.
        var ctx = Ctx("claude-opus-4.7-1m-internal", stream: false);
        ctx.OriginalRequestedModel = "claude-opus-4-8";

        var strategy = CcStrategy(new StubClient(BufferedResponse(copilotBody)), tracing: true, ctx: ctx);

        await strategy.ForwardAsync();

        // Snapshot the raw capture BEFORE the rewrite stage runs (as the endpoint does).
        var rawCapture = ctx.Response.RawUpstreamResponseBody;

        // Apply the model rewrite the way the inspection stage does on the
        // buffered path: run ModelRewriteDetector and write its replacement bytes
        // back to BufferedBody.
        var rewrite = new ModelRewriteDetector(
            new DetectorOrder<ModelRewriteDetector>(0),
            TestOptions.Snapshot(new ResponseModelRewriteOptions { Enabled = true }),
            ctx);
        rewrite.Begin();
        var action = rewrite.InspectBuffered(ctx.Response.BufferedBody!);
        if (action.Kind == DetectionActionKind.RewriteEvent)
        {
            ctx.Response.BufferedBody = Encoding.UTF8.GetBytes(action.Event.Data);
        }

        // Client sees the restored requested model…
        Assert.Contains("claude-opus-4-8", Encoding.UTF8.GetString(ctx.Response.BufferedBody!));
        // …but the upstream-resp capture is Copilot's untouched original bytes.
        Assert.NotNull(rawCapture);
        Assert.Equal(copilotBody, rawCapture);
        Assert.Contains("claude-opus-4.7-1m-internal", Encoding.UTF8.GetString(rawCapture!));
    }

    // ── Codex parallel path: same contract on /responses ──────────────────────

    /// <summary>
    /// Contract: the Codex (/responses) strategy must capture Copilot's raw
    /// Responses SSE just like /cc — the fix is required to be symmetric. The
    /// capture is the pre-T3 wire bytes, so it equals exactly what Copilot sent.
    /// </summary>
    [Fact]
    public async Task Codex_TracingOn_Streaming_CaptureEqualsRawCopilotBytes()
    {
        // A minimal Responses-shaped SSE body (the content is opaque to the tee).
        var raw = Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"type\":\"response.created\"}\n\n"
            + "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n");

        var ctx = Ctx("gpt-5.3-codex", stream: true);
        var strategy = new CopilotResponsesStrategy(
            new StubClient(StreamingResponse(raw)),
            new CodexModelProfileCatalog(),
            ctx,
            TestAudit.Create(true),
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        await strategy.ForwardAsync();
        await DrainAsync(ctx.Response.EventStream!);

        Assert.NotNull(ctx.Response.RawUpstreamResponseCapture);
        Assert.Equal(raw, ctx.Response.RawUpstreamResponseCapture!.ToArray());
    }

    /// <summary>
    /// Contract: Codex with tracing OFF also allocates no capture. Pairs with the
    /// /cc off-trace test to lock the zero-overhead promise on both endpoints.
    /// </summary>
    [Fact]
    public async Task Codex_TracingOff_Streaming_NoCapture()
    {
        var raw = Encoding.UTF8.GetBytes(
            "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n");

        var ctx = Ctx("gpt-5.3-codex", stream: true);
        var strategy = new CopilotResponsesStrategy(
            new StubClient(StreamingResponse(raw)),
            new CodexModelProfileCatalog(),
            ctx,
            TestAudit.Create(false),
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        await strategy.ForwardAsync();
        await DrainAsync(ctx.Response.EventStream!);

        Assert.Null(ctx.Response.RawUpstreamResponseCapture);
    }

    // ── Gap (d): Codex BUFFERED path stashes the raw reference too ────────────

    /// <summary>
    /// Contract: the Codex buffered (non-streaming) path must stash Copilot's raw
    /// response bytes for the audit, symmetric with /cc — "Codex symmetric" is a
    /// stated contract, so assert it directly rather than trust structural parity.
    /// </summary>
    [Fact]
    public async Task Codex_TracingOn_Buffered_StashesRawBody()
    {
        var copilotBody = Encoding.UTF8.GetBytes("""{"id":"resp_1","object":"response"}""");
        var ctx = Ctx("gpt-5.3-codex", stream: false);
        var strategy = new CopilotResponsesStrategy(
            new StubClient(BufferedResponse(copilotBody)),
            new CodexModelProfileCatalog(),
            ctx,
            TestAudit.Create(true),
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        await strategy.ForwardAsync();

        Assert.NotNull(ctx.Response.RawUpstreamResponseBody);
        Assert.Equal(copilotBody, ctx.Response.RawUpstreamResponseBody);
    }

    /// <summary>
    /// Contract: a successful buffered Responses object enters the Anthropic hub
    /// IR before response stages run, while the audit retains the exact upstream
    /// bytes. This is what lets the same buffered detectors protect a Claude
    /// non-streaming recovery request.
    /// </summary>
    [Fact]
    public async Task Codex_BufferedSuccess_IsTranslatedToIr_BeforeResponseStages()
    {
        var copilotBody = Encoding.UTF8.GetBytes("""
        {"id":"resp_1","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"message","content":[{"type":"output_text","text":"safe text"}]}],
         "usage":{"input_tokens":3,"output_tokens":2}}
        """);
        var ctx = Ctx("gpt-5.6-sol", stream: false);
        var strategy = new CopilotResponsesStrategy(
            new StubClient(BufferedResponse(copilotBody)),
            new CodexModelProfileCatalog(),
            ctx,
            TestAudit.Create(true),
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        await strategy.ForwardAsync();

        var ir = Encoding.UTF8.GetString(ctx.Response.BufferedBody!);
        Assert.Contains("\"type\":\"message\"", ir);
        Assert.Contains("\"content\":[{\"type\":\"text\",\"text\":\"safe text\"}]", ir);
        Assert.DoesNotContain("\"object\":\"response\"", ir);
        Assert.Equal(copilotBody, ctx.Response.RawUpstreamResponseBody);
        Assert.Same(ctx.Response.RawUpstreamResponseBody, ctx.Response.BufferedResponsesWireBody);
        Assert.Equal(copilotBody, ctx.Response.BufferedResponsesWireBody);
        Assert.Same(ctx.Response.BufferedBody, ctx.Response.InitialBufferedIrBody);
    }

    // ── Gap (e): with the tee ON, the events the client sees are unperturbed ──

    /// <summary>
    /// Contract: turning the capture ON must not change a single byte of what the
    /// client receives. The tracing-on streaming test asserts capture==raw but
    /// discards the drained events; this asserts the *downstream* events are
    /// byte-identical to a no-tee parse, so a tee that captured correctly but
    /// corrupted the forwarded stream would be caught.
    /// </summary>
    [Fact]
    public async Task TracingOn_Streaming_ClientEventsIdenticalToNoTee()
    {
        var raw = SampleSse();
        var baseline = await DrainAsync(SseParser.Create(new MemoryStream(raw)).EnumerateAsync());

        var ctx = Ctx("claude-opus-4-8", stream: true);
        var strategy = CcStrategy(new StubClient(StreamingResponse(raw)), tracing: true, ctx: ctx);
        await strategy.ForwardAsync();
        var got = await DrainAsync(ctx.Response.EventStream!);

        Assert.Equal(baseline.Count, got.Count);
        for (var i = 0; i < baseline.Count; i++)
        {
            Assert.Equal(baseline[i].EventType, got[i].EventType);
            Assert.Equal(baseline[i].Data, got[i].Data);
        }
    }

    // ── Gap (b): mid-stream upstream fault → partial capture is still recorded ─

    // A stream that yields `prefix` then throws on the next read, simulating a
    // mid-stream upstream disconnect.
    private sealed class FaultingStream(byte[] prefix) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            if (_pos >= prefix.Length) throw new IOException("simulated mid-stream disconnect");
            var n = Math.Min(buffer.Length, prefix.Length - _pos);
            prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static HttpResponseMessage FaultingStreamingResponse(byte[] prefix)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FaultingStream(prefix)) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    /// <summary>
    /// Contract: when the upstream stream faults mid-way, the /cc capture must
    /// still hold the bytes received before the fault (so upstream-resp shows a
    /// partial body, not nothing), and the fault must propagate to the caller
    /// (the /cc strategy does not swallow it).
    /// </summary>
    [Fact]
    public async Task Cc_MidStreamFault_PartialCaptureKept_AndFaultPropagates()
    {
        // A complete first event, then the stream dies before any terminator.
        var prefix = Encoding.UTF8.GetBytes(
            "event: message_start\ndata: {\"type\":\"message_start\"}\n\n");
        var ctx = Ctx("claude-opus-4-8", stream: true);
        var strategy = CcStrategy(new StubClient(FaultingStreamingResponse(prefix)), tracing: true, ctx: ctx);

        await strategy.ForwardAsync();

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await DrainAsync(ctx.Response.EventStream!));

        // Partial bytes captured before the fault are retained.
        Assert.NotNull(ctx.Response.RawUpstreamResponseCapture);
        var captured = ctx.Response.RawUpstreamResponseCapture!.ToArray();
        Assert.NotEmpty(captured);
        Assert.Equal(prefix, captured);
    }

    /// <summary>
    /// Contract: the Responses strategy propagates a mid-stream fault so the
    /// downstream edge selects the client-native error shape. The partial raw
    /// upstream capture remains available after that exception.
    /// </summary>
    [Fact]
    public async Task Responses_MidStreamFault_Propagates_AndPartialCaptureIsKept()
    {
        var prefix = Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"type\":\"response.created\"}\n\n");
        var ctx = Ctx("gpt-5.3-codex", stream: true);
        var strategy = new CopilotResponsesStrategy(
            new StubClient(FaultingStreamingResponse(prefix)),
            new CodexModelProfileCatalog(),
            ctx,
            TestAudit.Create(true),
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        await strategy.ForwardAsync();
        await Assert.ThrowsAsync<IOException>(async () =>
            await DrainAsync(ctx.Response.EventStream!));

        Assert.NotNull(ctx.Response.RawUpstreamResponseCapture);
        Assert.Equal(prefix, ctx.Response.RawUpstreamResponseCapture!.ToArray());
    }
}

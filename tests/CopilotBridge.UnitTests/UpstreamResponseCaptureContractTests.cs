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

    private static CopilotMessagesPassthroughStrategy CcStrategy(ICopilotClient client, bool tracing) =>
        new(client, Options.Create(new TracingOptions { Enabled = tracing }),
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
        var strategy = CcStrategy(new StubClient(StreamingResponse(raw)), tracing: true);
        var ctx = Ctx("claude-opus-4-8", stream: true);

        await strategy.ForwardAsync(ctx);
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

        var strategy = CcStrategy(new StubClient(StreamingResponse(raw)), tracing: false);
        var ctx = Ctx("claude-opus-4-8", stream: true);

        await strategy.ForwardAsync(ctx);
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
        var strategy = CcStrategy(new StubClient(BufferedResponse(copilotBody)), tracing: true);

        // Realistic routing state: the router already rewrote Body.Model to the
        // resolved back-end variant and recorded the original the client asked for.
        // ResponseModelRewriteStage only fires when resolved != original, so the
        // resolved model must be the variant here — not the requested name.
        var ctx = Ctx("claude-opus-4.7-1m-internal", stream: false);
        ctx.OriginalRequestedModel = "claude-opus-4-8";

        await strategy.ForwardAsync(ctx);

        // Snapshot the raw capture BEFORE the rewrite stage runs (as the endpoint does).
        var rawCapture = ctx.Response.RawUpstreamResponseBody;

        var rewrite = new ResponseModelRewriteStage(
            NullLogger<ResponseModelRewriteStage>.Instance,
            Options.Create(new ResponseModelRewriteOptions { Enabled = true }));
        await rewrite.ApplyAsync(ctx);

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

        var strategy = new CopilotResponsesStrategy(
            new StubClient(StreamingResponse(raw)),
            new CodexModelProfileCatalog(),
            Options.Create(new TracingOptions { Enabled = true }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        var ctx = Ctx("gpt-5.3-codex", stream: true);

        await strategy.ForwardAsync(ctx);
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

        var strategy = new CopilotResponsesStrategy(
            new StubClient(StreamingResponse(raw)),
            new CodexModelProfileCatalog(),
            Options.Create(new TracingOptions { Enabled = false }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        var ctx = Ctx("gpt-5.3-codex", stream: true);

        await strategy.ForwardAsync(ctx);
        await DrainAsync(ctx.Response.EventStream!);

        Assert.Null(ctx.Response.RawUpstreamResponseCapture);
    }
}

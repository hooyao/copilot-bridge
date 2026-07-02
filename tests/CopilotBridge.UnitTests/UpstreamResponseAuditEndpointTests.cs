using System.Net;
using System.Text;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// END-TO-END coverage of the headline contract: the <c>upstream-resp</c> trace
/// artifact the bridge WRITES must equal Copilot's raw wire bytes. The
/// strategy-level tests stop at <c>ctx.Response.*</c>; these drive the real
/// <see cref="ClaudeCodeMessagesEndpoint.HandleAsync"/> through the real
/// <see cref="CopilotMessagesPassthroughStrategy"/> (with a stubbed Copilot HTTP
/// client) and assert the recorded <c>upstream-resp</c> body. This is the only
/// layer that exercises the finally-block selection AND the lazy-streaming
/// ordering invariant (the relay loop must drain the capture before the finally
/// finalizes it) — break either and streaming upstream-resp silently goes empty
/// again, which no other test would catch.
/// </summary>
public class UpstreamResponseAuditEndpointTests
{
    // Real passthrough strategy fed a canned HTTP response — so the endpoint's
    // streaming relay + finally see genuine capture state, not a test double.
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

    // Minimal runner mirroring production: resolve target, run the real strategy,
    // then apply response stages — enough to exercise the endpoint's relay/finally.
    private sealed class Runner(
        IUpstreamStrategy<MessagesRequest> strategy,
        IReadOnlyList<IResponseStage<MessagesRequest>> stages,
        string? originalModel) : IPipelineRunner<MessagesRequest>
    {
        public async Task RunAsync(Pipeline<MessagesRequest> pipeline, BridgeContext<MessagesRequest> ctx)
        {
            ctx.OriginalRequestedModel = originalModel ?? ctx.Request.Body.Model;
            ctx.Target = new RouteTarget(BackendVendor.CopilotAnthropic, "/v1/messages", ctx.Request.Body.Model);
            await strategy.ForwardAsync(ctx);
            foreach (var s in stages) await s.ApplyAsync(ctx);
        }
    }

    private static HttpResponseMessage StreamingResponse(byte[] sse)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(sse)) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    private static HttpResponseMessage BufferedResponse(byte[] body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return resp;
    }

    private static byte[] SampleSse() => Encoding.UTF8.GetBytes(
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-4.8\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n"
        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

    /// <summary>
    /// Drive <see cref="ClaudeCodeMessagesEndpoint.HandleAsync"/> end-to-end and
    /// return the body bytes the endpoint logged for the <c>upstream-resp</c>
    /// artifact (null if none was logged).
    /// </summary>
    private static async Task<byte[]?> RunAndGetUpstreamRespBody(
        string requestJson,
        HttpResponseMessage copilotResp,
        bool tracingEnabled,
        string? originalModel = null,
        IReadOnlyList<IResponseStage<MessagesRequest>>? stages = null)
    {
        stages ??= [];
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var tracing = Options.Create(new TracingOptions { Enabled = tracingEnabled });
        var strategy = new CopilotMessagesPassthroughStrategy(
            new StubClient(copilotResp), tracing,
            NullLogger<CopilotMessagesPassthroughStrategy>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        http.Response.Body = new MemoryStream();

        await ClaudeCodeMessagesEndpoint.HandleAsync(
            http,
            new Runner(strategy, stages, originalModel),
            new Pipeline<MessagesRequest>
            {
                Name = "test",
                RequestStages = [],
                ResponseStages = stages,
                Strategies = new StrategyRegistry<MessagesRequest>([]),
            },
            new ClaudeCodeInboundAdapter(NullLogger<ClaudeCodeInboundAdapter>.Instance),
            new ClaudeCodeOutboundAdapter(NullLogger<ClaudeCodeOutboundAdapter>.Instance),
            new ModelProfileCatalog(),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            tracing,
            loggerFactory.CreateLogger<MessagesRequest>(),
            NullLogger<ClaudeCodeMessagesEndpointTag>.Instance);

        return recorder.Events
            .Select(e => e.Properties.TryGetValue("Payload", out var p) ? p as BridgeIoPayload : null)
            .FirstOrDefault(p => p is { Kind: "upstream-resp" })?.Body;
    }

    /// <summary>
    /// Contract: with tracing on, a STREAMING request makes the endpoint write an
    /// upstream-resp whose body is byte-for-byte Copilot's raw SSE. Proves the
    /// finally reads the finalized streaming capture in the right order — the
    /// single most fragile thing the PR fixes.
    /// </summary>
    [Fact]
    public async Task Streaming_TracingOn_UpstreamRespBodyEqualsRawSse()
    {
        var raw = SampleSse();
        var requestJson =
            """{"model":"claude-opus-4-8","max_tokens":16,"stream":true,"messages":[{"role":"user","content":"x"}]}""";

        var body = await RunAndGetUpstreamRespBody(requestJson, StreamingResponse(raw), tracingEnabled: true);

        Assert.NotNull(body);
        Assert.Equal(raw, body);
    }

    /// <summary>
    /// Contract: a BUFFERED response whose model the pipeline rewrites must still
    /// be audited as Copilot's ORIGINAL bytes. Runs the REAL response inspection
    /// stage (model-rewrite detector active) so the audited body must diverge from
    /// the (rewritten) client-facing body.
    /// </summary>
    [Fact]
    public async Task Buffered_AfterModelRewrite_UpstreamRespKeepsCopilotBytes()
    {
        // Client asked for opus-4-8; router resolved to the back-end variant
        // opus-4.8 (request body below); rewrite restores 4-8 for the client.
        var copilotBody = Encoding.UTF8.GetBytes("""{"model":"claude-opus-4.8","type":"message"}""");
        var requestJson =
            """{"model":"claude-opus-4.8","max_tokens":16,"stream":false,"messages":[{"role":"user","content":"x"}]}""";
        // Inspection stage with model-rewrite enabled and the tool-leak guard off,
        // isolating the rewrite behavior this test asserts.
        var factory = new DetectorSetFactory(
            Options.Create(new ResponseModelRewriteOptions { Enabled = true }),
            Options.Create(new ToolLeakGuardOptions { Enabled = false }),
            NullLogger<ToolLeakDetector>.Instance);
        var stages = new IResponseStage<MessagesRequest>[]
        {
            new ResponseInspectionStage(factory, NullLogger<ResponseInspectionStage>.Instance),
        };

        var body = await RunAndGetUpstreamRespBody(
            requestJson, BufferedResponse(copilotBody), tracingEnabled: true,
            originalModel: "claude-opus-4-8", stages: stages);

        Assert.NotNull(body);
        // The audit holds Copilot's original bytes, NOT the rewritten model.
        Assert.Equal(copilotBody, body);
        Assert.Contains("claude-opus-4.8", Encoding.UTF8.GetString(body!));
    }
}

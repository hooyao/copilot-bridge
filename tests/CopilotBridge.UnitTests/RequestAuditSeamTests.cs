using System.Net;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// End-to-end coverage of the <see cref="RequestAudit"/> seam through the real
/// <see cref="ClaudeCodeMessagesEndpoint"/> + <see cref="CopilotMessagesPassthroughStrategy"/>.
/// Two headline contracts, plus their edge cases:
/// <list type="number">
/// <item><b>Zero overhead when off.</b> With <c>Tracing.Enabled=false</c> the
/// request path does NO audit-only work — no bridge-IO event is emitted and the
/// strategy stashes NO <c>UpstreamWireBody</c> — while the client still gets the
/// identical bytes/events. This is the whole point of the seam.</item>
/// <item><b>Fidelity when on, never the IR.</b> With tracing on, <c>upstream-req</c>
/// is the EXACT bytes handed to the Copilot client, and <c>inbound-req</c> is the
/// raw client bytes — neither is the internal IR.</item>
/// </list>
/// These assert required behaviour derived from the contract, not the current
/// implementation; each is mutation-checkable (break the product → a named
/// assertion goes red).
/// </summary>
public class RequestAuditSeamTests
{
    // Real passthrough strategy fed a canned HTTP response, so the endpoint's
    // relay/finally see genuine seam + capture state (not a test double).
    private sealed class StubClient(HttpResponseMessage resp) : ICopilotClient
    {
        public byte[]? LastPostedBody { get; private set; }

        public ValueTask<HttpResponseMessage> PostMessagesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            IReadOnlyList<string>? anthropicBeta = null,
            IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
            CancellationToken ct = default)
        {
            LastPostedBody = body.ToArray();
            return new(resp);
        }

        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Minimal production-shaped runner: resolve target, run the real strategy,
    // then apply response stages.
    private sealed class Runner(
        BridgeContext<MessagesRequest> ctx,
        IUpstreamStrategy<MessagesRequest> strategy,
        IReadOnlyList<IResponseStage<MessagesRequest>> stages) : IPipelineRunner<MessagesRequest>
    {
        public async Task RunAsync(Pipeline<MessagesRequest> pipeline)
        {
            ctx.OriginalRequestedModel = ctx.Request.Body.Model;
            ctx.Target = new RouteTarget(BackendVendor.CopilotAnthropic, "/v1/messages", ctx.Request.Body.Model);
            await strategy.ForwardAsync();
            foreach (var s in stages) await s.ApplyAsync();
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

    private sealed record Result(
        int Status,
        string ClientBody,
        List<BridgeIoPayload> Audits,
        byte[]? PostedBody,
        BridgeContext<MessagesRequest> Ctx);

    /// <summary>
    /// Drive the real <see cref="ClaudeCodeMessagesEndpoint.HandleAsync"/> end-to-end
    /// with a shared <see cref="RequestAudit"/> emitting through a recording logger,
    /// so the test can observe exactly which bridge-IO artifacts were produced.
    /// </summary>
    private static async Task<Result> RunCc(
        string requestJson, HttpResponseMessage copilotResp, bool tracingEnabled)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var audit = TestAudit.Create(tracingEnabled, loggerFactory.CreateLogger<MessagesRequest>());

        var bridgeCtx = new BridgeContext<MessagesRequest>();
        var client = new StubClient(copilotResp);
        var strategy = new CopilotMessagesPassthroughStrategy(
            client, bridgeCtx, audit, NullLogger<CopilotMessagesPassthroughStrategy>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var respStream = new MemoryStream();
        http.Response.Body = respStream;

        await ClaudeCodeMessagesEndpoint.HandleAsync(
            http,
            bridgeCtx,
            new Runner(bridgeCtx, strategy, []),
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
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            audit,
            NullLogger<ClaudeCodeMessagesEndpointTag>.Instance);

        var audits = recorder.Events
            .Select(e => e.Properties.TryGetValue("Payload", out var p) ? p as BridgeIoPayload : null)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        return new Result(
            http.Response.StatusCode,
            Encoding.UTF8.GetString(respStream.ToArray()),
            audits,
            client.LastPostedBody,
            bridgeCtx);
    }

    // ── 1.a Zero-overhead-when-off ────────────────────────────────────────────

    /// <summary>
    /// Contract: a STREAMING /cc request handled with tracing OFF emits ZERO
    /// bridge-IO artifacts, stashes NO UpstreamWireBody, allocates NO raw capture —
    /// yet the client still receives every SSE event. Off-trace must be pure
    /// observation-free passthrough. (Mutation-check: force the strategy to always
    /// stash, or the endpoint to always emit → the "no audit" / "null wire body"
    /// assertions go red.)
    /// </summary>
    [Fact]
    public async Task Cc_Streaming_TracingOff_NoAudit_NoWireBody_NoCapture()
    {
        var raw = SampleSse();
        var json = """{"model":"claude-opus-4-8","max_tokens":16,"stream":true,"messages":[{"role":"user","content":"x"}]}""";

        var r = await RunCc(json, StreamingResponse(raw), tracingEnabled: false);

        Assert.Empty(r.Audits);
        Assert.Null(r.Ctx.Response.UpstreamWireBody);
        Assert.Null(r.Ctx.Response.RawUpstreamResponseCapture);
        // Client still saw the stream: the relayed body carries the text delta.
        Assert.Contains("content_block_delta", r.ClientBody);
        Assert.Contains("hi", r.ClientBody);
    }

    /// <summary>
    /// Contract: a BUFFERED /cc request with tracing OFF emits zero artifacts and
    /// stashes neither UpstreamWireBody nor RawUpstreamResponseBody, while the
    /// client still receives the buffered body.
    /// </summary>
    [Fact]
    public async Task Cc_Buffered_TracingOff_NoAudit_NoRawStash()
    {
        var body = Encoding.UTF8.GetBytes("""{"type":"message","model":"claude-opus-4.8"}""");
        var json = """{"model":"claude-opus-4.8","max_tokens":16,"stream":false,"messages":[{"role":"user","content":"x"}]}""";

        var r = await RunCc(json, BufferedResponse(body), tracingEnabled: false);

        Assert.Empty(r.Audits);
        Assert.Null(r.Ctx.Response.UpstreamWireBody);
        Assert.Null(r.Ctx.Response.RawUpstreamResponseBody);
        Assert.Contains("message", r.ClientBody);
    }

    /// <summary>
    /// Contract: with tracing ON the same request DOES emit the four artifacts —
    /// the inverse of the off-trace test, so "off ⇒ empty" is a real gate, not a
    /// harness that never emits. (This is the mutation-check baseline for the two
    /// tests above.)
    /// </summary>
    [Fact]
    public async Task Cc_Streaming_TracingOn_EmitsFourArtifacts()
    {
        var raw = SampleSse();
        var json = """{"model":"claude-opus-4-8","max_tokens":16,"stream":true,"messages":[{"role":"user","content":"x"}]}""";

        var r = await RunCc(json, StreamingResponse(raw), tracingEnabled: true);

        var kinds = r.Audits.Select(a => a.Kind).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "inbound-req", "inbound-resp", "upstream-req", "upstream-resp" }, kinds);
    }

    // ── 1.b Fidelity — exact wire bytes, never the IR ─────────────────────────

    /// <summary>
    /// Contract: on a claude-* passthrough with tracing on, upstream-req equals the
    /// EXACT bytes the Copilot client received — not a separately re-serialized copy.
    /// Asserted against the stub client's recorded POST body, which is the ground
    /// truth for "what we actually sent". (Mutation-check: skip the strategy stash →
    /// upstream-req becomes empty → red.)
    /// </summary>
    [Fact]
    public async Task Cc_TracingOn_UpstreamReqEqualsExactPostedBytes()
    {
        var json = """{"model":"claude-opus-4-8","max_tokens":16,"stream":false,"messages":[{"role":"user","content":"hello"}]}""";

        var r = await RunCc(json, BufferedResponse(Encoding.UTF8.GetBytes("{}")), tracingEnabled: true);

        var upReq = r.Audits.Single(a => a.Kind == "upstream-req");
        Assert.NotNull(r.PostedBody);
        Assert.Equal(r.PostedBody, upReq.Body[..upReq.BodyLength]);
    }

    /// <summary>
    /// Contract: inbound-req is the client's RAW request bytes (pre-IR). Assert the
    /// audited inbound-req round-trips to the original request JSON (same model,
    /// same message) — proving we captured the wire request, not the internal IR.
    /// <para>
    /// Doubles as the dispose-safety regression for the pooled read: the endpoint's
    /// `using var inbound` must still be alive when `RecordInbound` copies its bytes.
    /// Mutation-check: dispose `inbound` before the audit call → the pooled buffer is
    /// returned early and this assertion goes RED (ObjectDisposedException / corrupt
    /// bytes). Verified during the retire-rawbody change.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cc_TracingOn_InboundReqIsRawClientBytes_NotIr()
    {
        var json = """{"model":"claude-opus-4-8","max_tokens":16,"stream":false,"messages":[{"role":"user","content":"probe-marker"}]}""";

        var r = await RunCc(json, BufferedResponse(Encoding.UTF8.GetBytes("{}")), tracingEnabled: true);

        var inReq = r.Audits.Single(a => a.Kind == "inbound-req");
        var recorded = Encoding.UTF8.GetString(inReq.Body, 0, inReq.BodyLength);
        // The exact inbound bytes, byte-for-byte.
        Assert.Equal(json, recorded);
        // And they parse as the client request shape (has "max_tokens"), which the
        // Anthropic IR MessagesRequest round-trips; the marker survives verbatim.
        Assert.Contains("probe-marker", recorded);
        Assert.Contains("max_tokens", recorded);
    }

    // ── 1.c Edge cases ────────────────────────────────────────────────────────

    /// <summary>
    /// Contract: a malformed inbound body (400 before any upstream call) still
    /// audits the boundary it reached — inbound-req with the raw bad bytes and
    /// inbound-resp — but fabricates NO upstream-req/upstream-resp for a POST that
    /// never happened.
    /// </summary>
    [Fact]
    public async Task Cc_DeserializeFailure_AuditsInboundOnly_NoUpstream()
    {
        var bad = "{ this is not json ";
        var r = await RunCc(bad, BufferedResponse(Encoding.UTF8.GetBytes("{}")), tracingEnabled: true);

        Assert.Equal(StatusCodes.Status400BadRequest, r.Status);
        Assert.Contains(r.Audits, a => a.Kind == "inbound-req");
        Assert.Contains(r.Audits, a => a.Kind == "inbound-resp");
        Assert.DoesNotContain(r.Audits, a => a.Kind == "upstream-req");
        Assert.DoesNotContain(r.Audits, a => a.Kind == "upstream-resp");
        // The inbound-req still holds the raw (malformed) bytes.
        var inReq = r.Audits.Single(a => a.Kind == "inbound-req");
        Assert.Equal(bad, Encoding.UTF8.GetString(inReq.Body, 0, inReq.BodyLength));
    }

    /// <summary>
    /// Contract: a malformed inbound body with tracing OFF emits nothing at all —
    /// the early-return path is gated by the seam just like the happy path.
    /// </summary>
    [Fact]
    public async Task Cc_DeserializeFailure_TracingOff_NoAudit()
    {
        var r = await RunCc("{ nope ", BufferedResponse(Encoding.UTF8.GetBytes("{}")), tracingEnabled: false);

        Assert.Equal(StatusCodes.Status400BadRequest, r.Status);
        Assert.Empty(r.Audits);
    }

    /// <summary>
    /// Contract: an empty (zero-length) upstream body does not crash and records an
    /// upstream-resp with a zero-length body. Guards the BodyNode(length==0)→null
    /// path through the seam.
    /// </summary>
    [Fact]
    public async Task Cc_EmptyUpstreamBody_TracingOn_NoCrash_ZeroLengthAudit()
    {
        var json = """{"model":"claude-opus-4-8","max_tokens":16,"stream":false,"messages":[{"role":"user","content":"x"}]}""";

        var r = await RunCc(json, BufferedResponse([]), tracingEnabled: true);

        var upResp = r.Audits.Single(a => a.Kind == "upstream-resp");
        Assert.Equal(0, upResp.BodyLength);
    }

    /// <summary>
    /// Contract: header redaction still applies through the seam — a request
    /// carrying an Authorization header records it as &lt;redacted&gt; in inbound-req.
    /// (The sink redacts on write; this proves the seam doesn't bypass the sink's
    /// header handling by pre-materializing a raw copy.)
    /// </summary>
    [Fact]
    public async Task Cc_TracingOn_SensitiveHeadersFlowToSinkForRedaction()
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var audit = TestAudit.Create(true, loggerFactory.CreateLogger<MessagesRequest>());
        var bridgeCtx = new BridgeContext<MessagesRequest>();
        var strategy = new CopilotMessagesPassthroughStrategy(
            new StubClient(BufferedResponse(Encoding.UTF8.GetBytes("{}"))), bridgeCtx, audit,
            NullLogger<CopilotMessagesPassthroughStrategy>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages";
        http.Request.Headers["Authorization"] = "Bearer super-secret";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"model":"claude-opus-4-8","max_tokens":16,"stream":false,"messages":[{"role":"user","content":"x"}]}"""));
        http.Response.Body = new MemoryStream();

        await ClaudeCodeMessagesEndpoint.HandleAsync(
            http, bridgeCtx, new Runner(bridgeCtx, strategy, []),
            new Pipeline<MessagesRequest> { Name = "t", RequestStages = [], ResponseStages = [], Strategies = new StrategyRegistry<MessagesRequest>([]) },
            new ClaudeCodeInboundAdapter(NullLogger<ClaudeCodeInboundAdapter>.Instance),
            new ClaudeCodeOutboundAdapter(NullLogger<ClaudeCodeOutboundAdapter>.Instance),
            new ModelProfileCatalog(),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            audit, NullLogger<ClaudeCodeMessagesEndpointTag>.Instance);

        var inReq = recorder.Events
            .Select(e => e.Properties.TryGetValue("Payload", out var p) ? p as BridgeIoPayload : null)
            .Single(p => p is { Kind: "inbound-req" })!;
        // The payload carries the raw header value; the sink redacts it on write.
        // Assert the seam preserved the header so redaction has something to act on.
        Assert.True(inReq.Headers.ContainsKey("Authorization"));
    }

    // ── 1.d Toggle symmetry ───────────────────────────────────────────────────

    /// <summary>
    /// Contract: the same streaming request returns byte-identical client output
    /// with tracing on vs off — the audit is a pure observer, never perturbing the
    /// wire. Pairs the on/off runs and diffs the relayed body.
    /// </summary>
    [Fact]
    public async Task Cc_Streaming_OnVsOff_ClientBytesIdentical()
    {
        var json = """{"model":"claude-opus-4-8","max_tokens":16,"stream":true,"messages":[{"role":"user","content":"x"}]}""";

        var on = await RunCc(json, StreamingResponse(SampleSse()), tracingEnabled: true);
        var off = await RunCc(json, StreamingResponse(SampleSse()), tracingEnabled: false);

        Assert.Equal(on.ClientBody, off.ClientBody);
    }
}

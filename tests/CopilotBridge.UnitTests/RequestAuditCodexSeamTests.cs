using System.Net;
using System.Text;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Endpoints.Codex;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// End-to-end coverage of the audit seam on the Codex (`/responses`) edge, driving
/// the REAL <see cref="CodexResponsesEndpoint.HandleAsync"/> through the REAL T1
/// inbound adapter + the REAL <see cref="CopilotResponsesStrategy"/> (T2) with a
/// stub Copilot client. The point is the property the user probed: on a Codex
/// request each artifact is the WIRE on its boundary, never the internal IR —
/// <c>inbound-req</c> is the untranslated Codex request (pre-T1) and
/// <c>upstream-req</c> is the T2 Responses body (the exact bytes POSTed), and the
/// two are deliberately NOT byte-equal.
/// </summary>
public class RequestAuditCodexSeamTests
{
    private sealed class StubClient(HttpResponseMessage resp) : ICopilotClient
    {
        public byte[]? LastResponsesBody { get; private set; }

        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default)
        {
            LastResponsesBody = body.ToArray();
            return new(resp);
        }

        public ValueTask<HttpResponseMessage> PostMessagesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            IReadOnlyList<string>? anthropicBeta = null,
            IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Runner that runs the real Codex strategy after resolving a CopilotResponses
    // target — the strategy is what stashes UpstreamWireBody (T2 output).
    private sealed class Runner(
        BridgeContext<MessagesRequest> ctx,
        CopilotResponsesStrategy strategy) : IPipelineRunner<MessagesRequest>
    {
        public async Task RunAsync(Pipeline<MessagesRequest> pipeline)
        {
            ctx.OriginalRequestedModel = ctx.Request.Body.Model;
            ctx.Target = new RouteTarget(
                BackendVendor.CopilotResponses, "/responses", ctx.Request.Body.Model);
            await strategy.ForwardAsync();
        }
    }

    private static HttpResponseMessage BufferedResponse(byte[] body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return resp;
    }

    private const string CodexRequest =
        """{"model":"gpt-5.3-codex","instructions":"sys","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"codex-probe"}]}],"reasoning":{"effort":"high"}}""";

    private sealed record Result(List<BridgeIoPayload> Audits, byte[]? PostedBody);

    private static async Task<Result> RunCodex(string requestJson, bool tracingEnabled)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var audit = TestAudit.Create(tracingEnabled, loggerFactory.CreateLogger<MessagesRequest>());

        var bridgeCtx = new BridgeContext<MessagesRequest>();
        var client = new StubClient(BufferedResponse(Encoding.UTF8.GetBytes("""{"type":"response","status":"completed"}""")));
        var strategy = new CopilotResponsesStrategy(
            client, new CodexModelProfileCatalog(), bridgeCtx, audit,
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotResponsesStrategy>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/codex/responses";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        http.Response.Body = new MemoryStream();

        await CodexResponsesEndpoint.HandleAsync(
            http,
            bridgeCtx,
            new Runner(bridgeCtx, strategy),
            new Pipeline<MessagesRequest> { Name = "t", RequestStages = [], ResponseStages = [], Strategies = new StrategyRegistry<MessagesRequest>([]) },
            new ResponsesToIrInboundAdapter(NullLogger<ResponsesToIrInboundAdapter>.Instance),
            new IrToResponsesOutboundAdapter(bridgeCtx, NullLogger<IrToResponsesOutboundAdapter>.Instance),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            audit,
            NullLogger<CodexResponsesEndpointTag>.Instance);

        var audits = recorder.Events
            .Select(e => e.Properties.TryGetValue("Payload", out var p) ? p as BridgeIoPayload : null)
            .Where(p => p is not null).Select(p => p!).ToList();
        return new Result(audits, client.LastResponsesBody);
    }

    private static string BodyText(BridgeIoPayload p) => Encoding.UTF8.GetString(p.Body, 0, p.BodyLength);

    /// <summary>
    /// Contract (1.b.2): the Codex `upstream-req` equals the EXACT Responses bytes
    /// the strategy POSTed (T2 output), captured from the stub client.
    /// </summary>
    [Fact]
    public async Task Codex_TracingOn_UpstreamReqEqualsPostedResponsesBytes()
    {
        var r = await RunCodex(CodexRequest, tracingEnabled: true);

        var upReq = r.Audits.Single(a => a.Kind == "upstream-req");
        Assert.NotNull(r.PostedBody);
        Assert.Equal(r.PostedBody, upReq.Body[..upReq.BodyLength]);
    }

    /// <summary>
    /// Contract (1.b.3): the Codex `inbound-req` is the untranslated client request
    /// (pre-T1) — byte-for-byte the original Responses JSON, NOT the Anthropic IR.
    /// </summary>
    [Fact]
    public async Task Codex_TracingOn_InboundReqIsRawCodexRequest_NotIr()
    {
        var r = await RunCodex(CodexRequest, tracingEnabled: true);

        var inReq = r.Audits.Single(a => a.Kind == "inbound-req");
        Assert.Equal(CodexRequest, BodyText(inReq));
        // Sanity: Responses-shape markers present; Anthropic IR markers absent.
        Assert.Contains("\"input\"", BodyText(inReq));
        Assert.Contains("\"instructions\"", BodyText(inReq));
        Assert.DoesNotContain("\"max_tokens\"", BodyText(inReq));
    }

    /// <summary>
    /// Contract (1.b.4): `inbound-req` and `upstream-req` are BOTH Responses-shaped
    /// and NEITHER is the internal Anthropic IR. A Codex request is translated twice
    /// (T1 client→IR, T2 IR→Copilot); the two artifacts are the wire on each side,
    /// never the hub. Note: for a trivial request T1∘T2 can be an identity, so the
    /// two Responses bodies may be byte-equal — byte-INEQUALITY is request-dependent
    /// and is deliberately NOT asserted here. What IS invariant: both are Responses
    /// (carry `input`/`instructions`), and neither carries the IR's `max_tokens`
    /// (which the Anthropic MessagesRequest always serializes). That is the guard
    /// against a regression that audits the IR as either artifact.
    /// </summary>
    [Fact]
    public async Task Codex_TracingOn_InboundAndUpstreamAreResponses_NeitherIsIr()
    {
        var r = await RunCodex(CodexRequest, tracingEnabled: true);

        var inReq = BodyText(r.Audits.Single(a => a.Kind == "inbound-req"));
        var upReq = BodyText(r.Audits.Single(a => a.Kind == "upstream-req"));

        // Both are Responses-native.
        Assert.Contains("\"input\"", inReq);
        Assert.Contains("\"input\"", upReq);
        // Neither is the Anthropic IR: the IR MessagesRequest always serializes
        // "max_tokens" (a required field), so its presence would mean we audited the
        // hub instead of the wire.
        Assert.DoesNotContain("\"max_tokens\"", inReq);
        Assert.DoesNotContain("\"max_tokens\"", upReq);
    }

    /// <summary>
    /// Contract (1.a.3): Codex end-to-end with tracing OFF emits zero artifacts and
    /// stashes no wire body — the seam gates the Codex edge exactly like /cc.
    /// </summary>
    [Fact]
    public async Task Codex_TracingOff_NoAudit()
    {
        var r = await RunCodex(CodexRequest, tracingEnabled: false);
        Assert.Empty(r.Audits);
    }
}

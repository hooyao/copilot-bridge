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

    private static HttpResponseMessage StreamingResponse(Stream body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    private sealed class SilentGapStream(byte[] prefix, TimeSpan silence, byte[] suffix) : Stream
    {
        private int _position;
        private bool _waited;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsSpan(_position, count).CopyTo(buffer.Span);
                _position += count;
                return count;
            }
            if (!_waited)
            {
                await Task.Delay(silence, cancellationToken);
                _waited = true;
            }
            var suffixOffset = _position - prefix.Length;
            if (suffixOffset >= suffix.Length) return 0;
            var suffixCount = Math.Min(buffer.Length, suffix.Length - suffixOffset);
            suffix.AsSpan(suffixOffset, suffixCount).CopyTo(buffer.Span);
            _position += suffixCount;
            return suffixCount;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private const string CodexRequest =
        """{"model":"gpt-5.3-codex","instructions":"sys","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"codex-probe"}]}],"reasoning":{"effort":"high"}}""";

    private sealed record Result(List<BridgeIoPayload> Audits, byte[]? PostedBody);

    private static async Task<Result> RunCodex(
        string requestJson,
        bool tracingEnabled,
        HttpResponseMessage? upstreamResponse = null,
        UpstreamTimeoutOptions? timeouts = null)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var audit = TestAudit.Create(tracingEnabled, loggerFactory.CreateLogger<MessagesRequest>());

        var bridgeCtx = new BridgeContext<MessagesRequest>();
        var client = new StubClient(upstreamResponse
            ?? BufferedResponse(Encoding.UTF8.GetBytes("""{"type":"response","status":"completed"}""")));
        var strategy = new CopilotResponsesStrategy(
            client, new CodexModelProfileCatalog(), bridgeCtx, audit,
            Options.Create(timeouts ?? new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 0,
                KeepAliveIntervalSeconds = 0,
            }),
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

    /// <summary>
    /// Contract: Copilot omits the reasoning-accounting header, so raw upstream
    /// evidence must remain absent while the native Codex client edge records the
    /// compatibility signal it actually delivered. This prevents implementing the
    /// fix inside the shared upstream header dictionary and falsifying the trace.
    /// </summary>
    [Fact]
    public async Task Codex_ReasoningIncludedHeader_IsDownstreamOnlyInAudit()
    {
        const string header = "X-Reasoning-Included";
        const string streamingRequest =
            """{"model":"gpt-5.3-codex","instructions":"sys","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"codex-probe"}]}],"reasoning":{"effort":"high"},"stream":true}""";
        var upstreamBytes = Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":1,"
            + "\"response\":{\"id\":\"resp_header\",\"model\":\"gpt-5.3-codex\"}}\n\n"
            + "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":2,"
            + "\"response\":{\"id\":\"resp_header\",\"status\":\"completed\",\"output\":[],"
            + "\"usage\":{\"input_tokens\":1,\"input_tokens_details\":{\"cached_tokens\":0},"
            + "\"output_tokens\":0,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":1}}}\n\n");
        var result = await RunCodex(
            streamingRequest,
            tracingEnabled: true,
            StreamingResponse(new MemoryStream(upstreamBytes)));

        var upstream = result.Audits.Single(a => a.Kind == "upstream-resp");
        var inbound = result.Audits.Single(a => a.Kind == "inbound-resp");

        Assert.False(upstream.Headers.ContainsKey(header));
        Assert.True(inbound.Headers.TryGetValue(header, out var value));
        Assert.Equal("true", value);
    }

    /// <summary>
    /// Contract: the native Codex edge records a bridge keepalive as injected while
    /// the raw upstream artifact stays byte-faithful. This drives the real endpoint,
    /// T1/T2/T3, response inspection seam, T4, writer flush, and audit capture.
    /// </summary>
    [Fact]
    public async Task Codex_StreamingKeepAlive_IsMarkedInjected_AndAbsentFromRawUpstream()
    {
        const string streamingRequest =
            """{"model":"gpt-5.6-sol","instructions":"sys","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"wait"}]}],"stream":true}""";
        var prefix = Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":1,"
            + "\"response\":{\"id\":\"resp_audit_ping\",\"model\":\"gpt-5.6-sol\"}}\n\n");
        var suffix = Encoding.UTF8.GetBytes(
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":2,"
            + "\"response\":{\"id\":\"resp_audit_ping\",\"status\":\"completed\",\"output\":[]}}\n\n");
        var upstream = new SilentGapStream(prefix, TimeSpan.FromMilliseconds(2200), suffix);

        var result = await RunCodex(
            streamingRequest,
            tracingEnabled: true,
            StreamingResponse(upstream),
            new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 30,
                KeepAliveIntervalSeconds = 1,
            });

        var inbound = result.Audits.Single(a => a.Kind == "inbound-resp");
        var pings = inbound.Events!.Where(e => e.EventType == "ping").ToList();
        Assert.NotEmpty(pings);
        Assert.All(pings, ping =>
        {
            Assert.True(ping.Injected);
            Assert.Equal("{\"type\":\"ping\"}", ping.Data);
        });

        var rawUpstream = BodyText(result.Audits.Single(a => a.Kind == "upstream-resp"));
        Assert.DoesNotContain("\"type\":\"ping\"", rawUpstream, StringComparison.Ordinal);
        Assert.Contains("response.created", rawUpstream, StringComparison.Ordinal);
        Assert.Contains("response.completed", rawUpstream, StringComparison.Ordinal);
    }
}

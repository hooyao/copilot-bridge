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
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Client-boundary contracts for a fault in the real Responses-to-IR stream.
/// These are deliberately endpoint tests rather than tests of a hand-built IR:
/// the production regression was caused by T3 converting a read exception into
/// a normal-looking IR terminal before the downstream endpoint could select the
/// caller's protocol.
///
/// Contract: Claude Code receives one retryable Anthropic SSE error (or an
/// operator-selected truncation), Codex receives exactly one
/// <c>response.failed</c>, the private T3/T4 failure marker never crosses
/// <c>/cc</c>, and every summary/audit records the actual upstream fault rather
/// than reporting a clean streaming 200.
/// </summary>
public class ResponsesStreamFaultEndpointContractTests
{
    private const string CcRequest =
        """{"model":"gpt-5.6-sol","max_tokens":128,"stream":true,"messages":[{"role":"user","content":"continue"}]}""";

    private const string CodexRequest =
        """{"model":"gpt-5.6-sol","instructions":"continue","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"continue"}]}],"stream":true,"store":false}""";

    private sealed record Outcome(
        int Status,
        string Body,
        IReadOnlyDictionary<string, object?> Summary,
        IReadOnlyList<BridgeIoPayload> Audits,
        IReadOnlyList<RecordedEvent> Logs);

    private sealed class StubClient(HttpResponseMessage response) : ICopilotClient
    {
        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) => new(response);

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

    private sealed class ResponsesRunner(
        BridgeContext<MessagesRequest> context,
        CopilotResponsesStrategy strategy) : IPipelineRunner<MessagesRequest>
    {
        public async Task RunAsync(Pipeline<MessagesRequest> pipeline)
        {
            context.OriginalRequestedModel = context.Request.Body.Model;
            context.Target = new RouteTarget(
                BackendVendor.CopilotResponses,
                "/responses",
                context.Request.Body.Model);
            await strategy.ForwardAsync();
            foreach (var stage in pipeline.ResponseStages)
                await stage.ApplyAsync();
        }
    }

    private sealed class WholeResponseBufferingDetector : IResponseDetector
    {
        public string Name => "WholeResponseBufferingContract";
        public int Order => 0;
        public bool Enabled => true;
        public bool RequiresBuffering => true;
        public void Begin() { }
        public DetectionAction InspectEvent(in System.Net.ServerSentEvents.SseItem<string> evt) =>
            DetectionAction.None;
    }

    private static readonly Pipeline<MessagesRequest> DummyPipeline = new()
    {
        Name = "responses-stream-fault-contract",
        RequestStages = [],
        ResponseStages = [],
        Strategies = new StrategyRegistry<MessagesRequest>([]),
    };

    /// <summary>
    /// Returns one complete Responses prefix and then throws the supplied fault on
    /// the next read. This reproduces the observable incident boundary (partial
    /// commentary followed by a failed read) without sleeping for the configured
    /// production idle budget.
    /// </summary>
    private sealed class FaultAfterPrefixStream(byte[] prefix, Exception fault) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_position >= prefix.Length) return ValueTask.FromException<int>(fault);
            var count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] PartialTextPrefix() => Encoding.UTF8.GetBytes(
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_fault\",\"status\":\"in_progress\"}}\n\n"
        + "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_partial\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}\n\n"
        + "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_partial\",\"output_index\":0,\"content_index\":0,\"delta\":\"I will update both specifications now.\"}\n\n");

    private static byte[] CompletedTextStream() => Encoding.UTF8.GetBytes(
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_ok\",\"status\":\"in_progress\"}}\n\n"
        + "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_ok\",\"role\":\"assistant\",\"content\":[]}}\n\n"
        + "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_ok\",\"output_index\":0,\"content_index\":0,\"delta\":\"done\"}\n\n"
        + "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_ok\",\"status\":\"completed\"}}\n\n"
        + "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_ok\",\"status\":\"completed\",\"usage\":{\"input_tokens\":4,\"output_tokens\":2}}}\n\n");

    // `server_error` is the captured Responses failure code already used by the
    // repository's protocol regression corpus. The upstream message is purposely
    // sensitive-looking: it must not be copied into a client error or bounded log.
    private static byte[] ExplicitFailedStream() => Encoding.UTF8.GetBytes(
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_failed\",\"status\":\"in_progress\"}}\n\n"
        + "event: response.failed\ndata: {\"type\":\"response.failed\",\"response\":{\"id\":\"resp_failed\",\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"secret generated response text\"}}}\n\n");

    private static HttpResponseMessage StreamingResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return response;
    }

    private static HttpResponseMessage BufferedResponse(byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return response;
    }

    private static CopilotResponsesStrategy BuildStrategy(
        ICopilotClient client,
        BridgeContext<MessagesRequest> context,
        RequestAudit audit,
        ILoggerFactory loggerFactory) =>
        new(
            client,
            new CodexModelProfileCatalog(),
            context,
            audit,
            Options.Create(new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 0,
            }),
            loggerFactory.CreateLogger<CopilotResponsesStrategy>());

    private static async Task<Outcome> RunClaudeAsync(
        Stream upstream,
        UpstreamTimeoutAction action = UpstreamTimeoutAction.Retry,
        bool tracing = true,
        bool wholeResponseBuffering = false)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var audit = TestAudit.Create(tracing, loggerFactory.CreateLogger<MessagesRequest>());
        var context = new BridgeContext<MessagesRequest>();
        var strategy = BuildStrategy(
            new StubClient(StreamingResponse(upstream)), context, audit, loggerFactory);

        var http = BuildHttpContext("/cc/v1/messages", CcRequest, out var responseBytes);
        var pipeline = wholeResponseBuffering
            ? PipelineWithWholeResponseBuffering(context, loggerFactory)
            : DummyPipeline;
        await ClaudeCodeMessagesEndpoint.HandleAsync(
            http,
            context,
            new ResponsesRunner(context, strategy),
            pipeline,
            new ClaudeCodeInboundAdapter(loggerFactory.CreateLogger<ClaudeCodeInboundAdapter>()),
            new ClaudeCodeOutboundAdapter(loggerFactory.CreateLogger<ClaudeCodeOutboundAdapter>()),
            new ModelProfileCatalog(),
            new RequestSummaryLogger(loggerFactory.CreateLogger<RequestSummaryLogger>()),
            audit,
            Options.Create(new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 0,
                StreamIdleAction = action,
                StreamIdleSignal = ResponseDetectionSignal.OverloadedError,
            }),
            loggerFactory.CreateLogger<ClaudeCodeMessagesEndpointTag>());

        return OutcomeFrom(http, responseBytes, recorder.Events);
    }

    private static async Task<Outcome> RunCodexAsync(Stream upstream, bool tracing = true)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var audit = TestAudit.Create(tracing, loggerFactory.CreateLogger<MessagesRequest>());
        var context = new BridgeContext<MessagesRequest>();
        var strategy = BuildStrategy(
            new StubClient(StreamingResponse(upstream)), context, audit, loggerFactory);

        var http = BuildHttpContext("/codex/responses", CodexRequest, out var responseBytes);
        await CodexResponsesEndpoint.HandleAsync(
            http,
            context,
            new ResponsesRunner(context, strategy),
            DummyPipeline,
            new ResponsesToIrInboundAdapter(loggerFactory.CreateLogger<ResponsesToIrInboundAdapter>()),
            new IrToResponsesOutboundAdapter(context, loggerFactory.CreateLogger<IrToResponsesOutboundAdapter>()),
            new RequestSummaryLogger(loggerFactory.CreateLogger<RequestSummaryLogger>()),
            audit,
            loggerFactory.CreateLogger<CodexResponsesEndpointTag>());

        return OutcomeFrom(http, responseBytes, recorder.Events);
    }

    private static async Task<Outcome> RunCodexAsync(
        Stream upstream,
        bool tracing,
        bool wholeResponseBuffering)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var audit = TestAudit.Create(tracing, loggerFactory.CreateLogger<MessagesRequest>());
        var context = new BridgeContext<MessagesRequest>();
        var strategy = BuildStrategy(
            new StubClient(StreamingResponse(upstream)), context, audit, loggerFactory);
        var pipeline = wholeResponseBuffering
            ? PipelineWithWholeResponseBuffering(context, loggerFactory)
            : DummyPipeline;

        var http = BuildHttpContext("/codex/responses", CodexRequest, out var responseBytes);
        await CodexResponsesEndpoint.HandleAsync(
            http,
            context,
            new ResponsesRunner(context, strategy),
            pipeline,
            new ResponsesToIrInboundAdapter(loggerFactory.CreateLogger<ResponsesToIrInboundAdapter>()),
            new IrToResponsesOutboundAdapter(context, loggerFactory.CreateLogger<IrToResponsesOutboundAdapter>()),
            new RequestSummaryLogger(loggerFactory.CreateLogger<RequestSummaryLogger>()),
            audit,
            loggerFactory.CreateLogger<CodexResponsesEndpointTag>());

        return OutcomeFrom(http, responseBytes, recorder.Events);
    }

    private static Pipeline<MessagesRequest> PipelineWithWholeResponseBuffering(
        BridgeContext<MessagesRequest> context,
        ILoggerFactory loggerFactory) => new()
    {
        Name = "whole-response-buffering-contract",
        RequestStages = [],
        ResponseStages =
        [
            new ResponseInspectionStage(
                [new WholeResponseBufferingDetector()],
                context,
                loggerFactory.CreateLogger<ResponseInspectionStage>()),
        ],
        Strategies = new StrategyRegistry<MessagesRequest>([]),
    };

    private static async Task<Outcome> RunClaudeBufferedAsync(
        byte[] upstreamBody,
        bool inspectForLeaks)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var audit = TestAudit.Create(true, loggerFactory.CreateLogger<MessagesRequest>());
        var context = new BridgeContext<MessagesRequest>();
        var strategy = BuildStrategy(
            new StubClient(BufferedResponse(upstreamBody)), context, audit, loggerFactory);
        var pipeline = inspectForLeaks
            ? PipelineWithLeakDetector(context, loggerFactory)
            : DummyPipeline;

        var http = BuildHttpContext(
            "/cc/v1/messages",
            CcRequest.Replace("\"stream\":true", "\"stream\":false", StringComparison.Ordinal),
            out var responseBytes);
        await ClaudeCodeMessagesEndpoint.HandleAsync(
            http,
            context,
            new ResponsesRunner(context, strategy),
            pipeline,
            new ClaudeCodeInboundAdapter(loggerFactory.CreateLogger<ClaudeCodeInboundAdapter>()),
            new ClaudeCodeOutboundAdapter(loggerFactory.CreateLogger<ClaudeCodeOutboundAdapter>()),
            new ModelProfileCatalog(),
            new RequestSummaryLogger(loggerFactory.CreateLogger<RequestSummaryLogger>()),
            audit,
            Options.Create(new UpstreamTimeoutOptions()),
            loggerFactory.CreateLogger<ClaudeCodeMessagesEndpointTag>());

        return OutcomeFrom(http, responseBytes, recorder.Events);
    }

    private static async Task<Outcome> RunCodexBufferedAsync(byte[] upstreamBody)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var audit = TestAudit.Create(true, loggerFactory.CreateLogger<MessagesRequest>());
        var context = new BridgeContext<MessagesRequest>();
        var strategy = BuildStrategy(
            new StubClient(BufferedResponse(upstreamBody)), context, audit, loggerFactory);

        var request = CodexRequest.Replace("\"stream\":true", "\"stream\":false", StringComparison.Ordinal);
        var http = BuildHttpContext("/codex/responses", request, out var responseBytes);
        await CodexResponsesEndpoint.HandleAsync(
            http,
            context,
            new ResponsesRunner(context, strategy),
            DummyPipeline,
            new ResponsesToIrInboundAdapter(loggerFactory.CreateLogger<ResponsesToIrInboundAdapter>()),
            new IrToResponsesOutboundAdapter(context, loggerFactory.CreateLogger<IrToResponsesOutboundAdapter>()),
            new RequestSummaryLogger(loggerFactory.CreateLogger<RequestSummaryLogger>()),
            audit,
            loggerFactory.CreateLogger<CodexResponsesEndpointTag>());

        return OutcomeFrom(http, responseBytes, recorder.Events);
    }

    private static Pipeline<MessagesRequest> PipelineWithLeakDetector(
        BridgeContext<MessagesRequest> context,
        ILoggerFactory loggerFactory)
    {
        var detector = new ResponseLeakDetector(
            new DetectorOrder<ResponseLeakDetector>(0),
            TestOptions.Snapshot(new ResponseLeakGuardOptions
            {
                Enabled = true,
                PreserveStream = true,
                Signal = ResponseDetectionSignal.OverloadedError,
            }),
            context,
            loggerFactory.CreateLogger<ResponseLeakDetector>());
        return new Pipeline<MessagesRequest>
        {
            Name = "buffered-responses-leak-contract",
            RequestStages = [],
            ResponseStages =
            [
                new ResponseInspectionStage(
                    [detector], context, loggerFactory.CreateLogger<ResponseInspectionStage>()),
            ],
            Strategies = new StrategyRegistry<MessagesRequest>([]),
        };
    }

    private static DefaultHttpContext BuildHttpContext(
        string path, string requestJson, out MemoryStream responseBytes)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));

        responseBytes = new MemoryStream();
        var feature = new StartTrackingResponseFeature();
        var tracking = new StartTrackingStream(responseBytes, feature);
        feature.Body = tracking;
        http.Features.Set<IHttpResponseFeature>(feature);
        http.Response.Body = tracking;
        return http;
    }

    private static Outcome OutcomeFrom(
        DefaultHttpContext http,
        MemoryStream responseBytes,
        IReadOnlyList<RecordedEvent> logs)
    {
        var summary = logs.Single(e =>
            e.Category == typeof(RequestSummaryLogger).FullName
            && e.Message.StartsWith("summary ", StringComparison.Ordinal));
        var audits = logs
            .Select(e => e.Properties.TryGetValue("Payload", out var value) ? value as BridgeIoPayload : null)
            .Where(payload => payload is not null)
            .Cast<BridgeIoPayload>()
            .ToArray();
        return new Outcome(
            http.Response.StatusCode,
            Encoding.UTF8.GetString(responseBytes.ToArray()),
            summary.Properties,
            audits,
            logs);
    }

    private static UpstreamTimeoutException StreamIdleFault() =>
        new(UpstreamTimeoutPhase.StreamIdle, TimeSpan.FromSeconds(60));

    /// <summary>
    /// A partial Responses turn is not a completed Anthropic turn. The Claude
    /// boundary must retain the partial text, append exactly one retryable error,
    /// and never append either the private failure stop reason or message_stop.
    /// </summary>
    [Fact]
    public async Task Claude_ResponseStreamTimeout_EmitsOneRetryableError_NotNormalTerminal()
    {
        var prefix = PartialTextPrefix();
        var outcome = await RunClaudeAsync(new FaultAfterPrefixStream(prefix, StreamIdleFault()));

        Assert.Equal(StatusCodes.Status200OK, outcome.Status);
        Assert.Contains("I will update both specifications now.", outcome.Body);
        Assert.Equal(1, Count(outcome.Body, "event: error"));
        Assert.Equal(1, Count(outcome.Body, "overloaded_error"));
        Assert.DoesNotContain("\"stop_reason\":\"error\"", outcome.Body);
        Assert.DoesNotContain("event: message_stop", outcome.Body);
    }

    /// <summary>
    /// A generic transport read failure is the same incomplete Responses turn at
    /// the Claude boundary. It must enter Claude's recovery path rather than end as
    /// a silent truncated 200 merely because the fault was not an idle timeout.
    /// </summary>
    [Fact]
    public async Task Claude_ResponseTransportFault_EmitsOneClientNativeError_NotSilentTruncation()
    {
        var outcome = await RunClaudeAsync(
            new FaultAfterPrefixStream(PartialTextPrefix(), new IOException("simulated disconnect")));

        Assert.Equal(StatusCodes.Status200OK, outcome.Status);
        Assert.Contains("I will update both specifications now.", outcome.Body);
        Assert.Equal(1, Count(outcome.Body, "event: error"));
        Assert.Equal(1, Count(outcome.Body, "api_error"));
        Assert.DoesNotContain("\"stop_reason\":\"error\"", outcome.Body);
        Assert.DoesNotContain("event: message_stop", outcome.Body);
        Assert.Contains(nameof(IOException), outcome.Summary["ErrorDisplay"]?.ToString());
    }

    [Fact]
    public async Task GenericResponseTransportFault_RemainsClientNativeOnBothEdges()
    {
        var claude = await RunClaudeAsync(
            new FaultAfterPrefixStream(PartialTextPrefix(), new IOException("claude disconnect")),
            UpstreamTimeoutAction.Truncate);
        Assert.DoesNotContain("event: error", claude.Body);
        Assert.DoesNotContain("event: message_stop", claude.Body);

        var codex = await RunCodexAsync(
            new FaultAfterPrefixStream(PartialTextPrefix(), new IOException("codex disconnect")));
        Assert.Equal(1, Count(codex.Body, "event: response.failed"));
        Assert.DoesNotContain("event: error", codex.Body);
        Assert.DoesNotContain("event: response.completed", codex.Body);
    }

    /// <summary>
    /// Truncate is an explicit operator policy: preserve bytes already delivered
    /// but append no error and no apparently-successful terminal.
    /// </summary>
    [Fact]
    public async Task Claude_ResponseStreamTimeout_Truncate_PreservesPartialWithoutTerminal()
    {
        var outcome = await RunClaudeAsync(
            new FaultAfterPrefixStream(PartialTextPrefix(), StreamIdleFault()),
            UpstreamTimeoutAction.Truncate);

        Assert.Equal(StatusCodes.Status200OK, outcome.Status);
        Assert.Contains("I will update both specifications now.", outcome.Body);
        Assert.DoesNotContain("event: error", outcome.Body);
        Assert.DoesNotContain("\"stop_reason\":\"error\"", outcome.Body);
        Assert.DoesNotContain("event: message_stop", outcome.Body);
    }

    /// <summary>
    /// T4 owns the Responses client surface. A throwing IR stream must yield one
    /// failed terminal before the exception reaches endpoint accounting; the
    /// already-started HTTP response remains 200.
    /// </summary>
    [Fact]
    public async Task Codex_ResponseStreamTimeout_EmitsExactlyOneFailedTerminal_StatusStays200()
    {
        var outcome = await RunCodexAsync(
            new FaultAfterPrefixStream(PartialTextPrefix(), StreamIdleFault()));

        Assert.Equal(StatusCodes.Status200OK, outcome.Status);
        Assert.Equal(1, Count(outcome.Body, "event: response.failed"));
        Assert.Equal(1, Count(outcome.Body, "\"type\":\"response.failed\""));
        Assert.DoesNotContain("event: error", outcome.Body);
        Assert.DoesNotContain("overloaded_error", outcome.Body);
        Assert.DoesNotContain("event: response.completed", outcome.Body);
        Assert.Equal("stream_idle", outcome.Summary["UpstreamTimeout"]?.ToString());
        Assert.NotEqual("(none)", outcome.Summary["ErrorDisplay"]?.ToString());
    }

    [Fact]
    public async Task WholeResponseBuffering_DoesNotBypassClientNativeFaultSurfaces()
    {
        var claude = await RunClaudeAsync(
            new FaultAfterPrefixStream(PartialTextPrefix(), StreamIdleFault()),
            wholeResponseBuffering: true);
        Assert.Equal(StatusCodes.Status200OK, claude.Status);
        Assert.Equal(1, Count(claude.Body, "event: error"));
        Assert.DoesNotContain("message_stop", claude.Body);

        var codex = await RunCodexAsync(
            new FaultAfterPrefixStream(PartialTextPrefix(), StreamIdleFault()),
            tracing: true,
            wholeResponseBuffering: true);
        Assert.Equal(StatusCodes.Status200OK, codex.Status);
        Assert.Equal(1, Count(codex.Body, "event: response.failed"));
        Assert.DoesNotContain("event: error", codex.Body);
    }

    [Fact]
    public async Task BufferedResponsesFallback_IsTranslatedBeforeLeakDetection()
    {
        var body = Encoding.UTF8.GetBytes("""
        {"id":"resp_leak","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"message","content":[{"type":"output_text","text":"<system-reminder>private control text</system-reminder>"}]}],
         "usage":{"input_tokens":4,"output_tokens":4}}
        """);

        var outcome = await RunClaudeBufferedAsync(body, inspectForLeaks: true);

        Assert.Equal(529, outcome.Status);
        Assert.Contains("overloaded_error", outcome.Body);
        Assert.DoesNotContain("\"object\":\"response\"", outcome.Body);
        Assert.DoesNotContain("private control text", outcome.Body);
    }

    [Fact]
    public async Task MalformedBufferedToolCall_ReturnsAnthropicError_NotRawResponses()
    {
        var body = Encoding.UTF8.GetBytes("""
        {"id":"resp_bad","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"function_call","call_id":"call_bad","name":"Bash","arguments":"{not-json"}]}
        """);

        var outcome = await RunClaudeBufferedAsync(body, inspectForLeaks: false);

        Assert.Equal(StatusCodes.Status502BadGateway, outcome.Status);
        using var doc = System.Text.Json.JsonDocument.Parse(outcome.Body);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("api_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.DoesNotContain("\"object\":\"response\"", outcome.Body);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"id\":\"resp_missing_status\",\"object\":\"response\",\"output\":[]}")]
    [InlineData("{\"id\":\"resp_nonterminal\",\"object\":\"response\",\"status\":\"in_progress\",\"output\":[]}")]
    public async Task UntranslatableSuccessfulBufferedResponse_FailsClosedAtClaudeEdge(string body)
    {
        var outcome = await RunClaudeBufferedAsync(Encoding.UTF8.GetBytes(body), inspectForLeaks: false);

        Assert.Equal(StatusCodes.Status502BadGateway, outcome.Status);
        using var doc = System.Text.Json.JsonDocument.Parse(outcome.Body);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("api_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.DoesNotContain("\"object\":\"response\"", outcome.Body);
    }

    [Fact]
    public async Task BufferedResponseFailed_IsClientNativeOnBothEdges()
    {
        var body = Encoding.UTF8.GetBytes("""
        {"id":"resp_failed","object":"response","status":"failed","model":"gpt-5.6-sol",
         "output":[],"error":{"code":"server_error","message":"secret generated response text"}}
        """);

        var claude = await RunClaudeBufferedAsync(body, inspectForLeaks: false);
        Assert.Equal(StatusCodes.Status502BadGateway, claude.Status);
        using (var doc = System.Text.Json.JsonDocument.Parse(claude.Body))
            Assert.Equal("api_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.DoesNotContain("secret generated response text", claude.Body);

        var codex = await RunCodexBufferedAsync(body);
        Assert.Equal(StatusCodes.Status502BadGateway, codex.Status);
        using (var doc = System.Text.Json.JsonDocument.Parse(codex.Body))
        {
            Assert.Equal("response", doc.RootElement.GetProperty("object").GetString());
            Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        }
        Assert.DoesNotContain("secret generated response text", codex.Body);
    }

    /// <summary>
    /// The new exceptional path is dormant for success: enabling the endpoint
    /// policy must not change any successful downstream SSE byte.
    /// </summary>
    [Fact]
    public async Task Claude_CompletedResponsesStream_IsByteIdenticalAcrossTimeoutActions()
    {
        var retry = await RunClaudeAsync(
            new MemoryStream(CompletedTextStream()), UpstreamTimeoutAction.Retry, tracing: false);
        var truncate = await RunClaudeAsync(
            new MemoryStream(CompletedTextStream()), UpstreamTimeoutAction.Truncate, tracing: false);

        Assert.Equal(retry.Body, truncate.Body);
        Assert.Contains("event: message_stop", retry.Body);
        Assert.DoesNotContain("event: error", retry.Body);
        Assert.Equal("(none)", retry.Summary["UpstreamTimeout"]?.ToString());
        Assert.Equal("(none)", retry.Summary["ErrorDisplay"]?.ToString());
    }

    /// <summary>
    /// Summary and trace artifacts are part of the failure contract. The raw
    /// upstream artifact keeps exact partial Responses bytes plus the error; the
    /// inbound artifact records the Anthropic error actually written to Claude.
    /// </summary>
    [Fact]
    public async Task Claude_ResponseStreamTimeout_RecordsTruthfulSummaryAndBothTraceSides()
    {
        var prefix = PartialTextPrefix();
        var outcome = await RunClaudeAsync(new FaultAfterPrefixStream(prefix, StreamIdleFault()));

        Assert.Equal("stream_idle", outcome.Summary["UpstreamTimeout"]?.ToString());
        Assert.NotEqual("(none)", outcome.Summary["ErrorDisplay"]?.ToString());

        var upstream = outcome.Audits.Single(a => a.Kind == "upstream-resp");
        Assert.Equal(prefix, upstream.Body.AsSpan(0, upstream.BodyLength).ToArray());
        Assert.Contains("stream_idle", upstream.Error, StringComparison.OrdinalIgnoreCase);

        var inbound = outcome.Audits.Single(a => a.Kind == "inbound-resp");
        Assert.Contains("stream_idle", inbound.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(inbound.Events);
        Assert.Single(inbound.Events!, e => e.EventType == "error" && e.Data.Contains("overloaded_error"));
        Assert.DoesNotContain(inbound.Events!, e => e.Data.Contains("\"stop_reason\":\"error\""));
        Assert.DoesNotContain(inbound.Events!, e => e.EventType == "message_stop");

        var warning = Assert.Single(outcome.Logs, e =>
            e.Category == typeof(ClaudeCodeMessagesEndpointTag).FullName
            && e.Level == LogLevel.Warning);
        Assert.Contains("phase=stream_idle", warning.Message);
        Assert.Contains(nameof(UpstreamTimeoutException), warning.Message);
        Assert.Contains("idle=60", warning.Message);
        Assert.DoesNotContain("I will update both specifications now.", warning.Message);
    }

    /// <summary>
    /// An explicit upstream response.failed is a stream failure, not an Anthropic
    /// stop reason. Claude gets an error event whose bounded text excludes the
    /// upstream generated message; Codex gets one native failed terminal.
    /// </summary>
    [Fact]
    public async Task ExplicitResponseFailed_IsClientNativeOnBothEdges_AndDoesNotLeakDetail()
    {
        var claude = await RunClaudeAsync(new MemoryStream(ExplicitFailedStream()));
        Assert.Equal(1, Count(claude.Body, "event: error"));
        Assert.DoesNotContain("\"stop_reason\":\"error\"", claude.Body);
        Assert.DoesNotContain("event: message_stop", claude.Body);
        Assert.DoesNotContain("secret generated response text", claude.Body);
        Assert.NotEqual("(none)", claude.Summary["ErrorDisplay"]?.ToString());

        var codex = await RunCodexAsync(new MemoryStream(ExplicitFailedStream()));
        Assert.Equal(StatusCodes.Status200OK, codex.Status);
        Assert.Equal(1, Count(codex.Body, "event: response.failed"));
        Assert.DoesNotContain("event: response.completed", codex.Body);
        Assert.DoesNotContain("secret generated response text", codex.Body);
        Assert.NotEqual("(none)", codex.Summary["ErrorDisplay"]?.ToString());
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private sealed class StartTrackingResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }
        public void MarkStarted() => HasStarted = true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class StartTrackingStream(Stream inner, StartTrackingResponseFeature feature) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            feature.MarkStarted();
            inner.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            feature.MarkStarted();
            return inner.WriteAsync(buffer, ct);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            feature.MarkStarted();
            return inner.WriteAsync(buffer, offset, count, ct);
        }
    }
}

using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Endpoints.Codex;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Offline coverage of <see cref="CodexResponsesEndpoint"/>'s error mapping
/// (change-3 review Gap 3). Drives <c>HandleAsync</c> through a constructed
/// <see cref="DefaultHttpContext"/> with a stub <see cref="IPipelineRunner{T}"/>
/// so every branch — deserialize-fail, the typed client/upstream exceptions, and
/// the happy buffered path — is exercised without Copilot creds or codex.exe.
/// The endpoint is the sole entry point for Codex; its status mapping is the
/// contract clients see.
/// </summary>
public class CodexEndpointTests
{
    // ── stub runner: each test supplies what the "pipeline" does ─────────────
    private sealed class StubRunner(Action<BridgeContext<MessagesRequest>> behavior)
        : IPipelineRunner<MessagesRequest>
    {
        public Task RunAsync(Pipeline<MessagesRequest> pipeline, BridgeContext<MessagesRequest> ctx)
        {
            behavior(ctx);
            return Task.CompletedTask;
        }
    }

    private static readonly Pipeline<MessagesRequest> DummyPipeline = new()
    {
        Name = "test",
        RequestStages = [],
        ResponseStages = [],
        Strategies = new StrategyRegistry<MessagesRequest>([]),
    };

    private static async Task<(int Status, string Body)> Invoke(
        string requestJson,
        Action<BridgeContext<MessagesRequest>> pipelineBehavior)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/codex/responses";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var respStream = new MemoryStream();
        http.Response.Body = respStream;

        await CodexResponsesEndpoint.HandleAsync(
            http,
            new StubRunner(pipelineBehavior),
            DummyPipeline,
            new ResponsesToIrInboundAdapter(NullLogger<ResponsesToIrInboundAdapter>.Instance),
            new IrToResponsesOutboundAdapter(NullLogger<IrToResponsesOutboundAdapter>.Instance),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            NullLogger<MessagesRequest>.Instance,
            NullLogger<CodexResponsesEndpointTag>.Instance);

        return (http.Response.StatusCode, Encoding.UTF8.GetString(respStream.ToArray()));
    }

    private const string ValidRequest = """
      {"model":"gpt-5.3-codex","instructions":"x",
       "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}],
       "stream":false,"store":false}
      """;

    // ── deserialize failures → 400 ───────────────────────────────────────────

    [Fact]
    public async Task MalformedJsonBody_Returns400()
    {
        var (status, body) = await Invoke("{not valid json", _ => { });
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("invalid request body", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullBody_Returns400()
    {
        // JSON literal null deserializes to a null ResponsesRequest.
        var (status, _) = await Invoke("null", _ => { });
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    // ── typed client/upstream exceptions from the pipeline → mapped status ────

    [Fact]
    public async Task UnknownModel_Returns400_WithAnthropicErrorEnvelope()
    {
        var (status, body) = await Invoke(ValidRequest, _ =>
            throw new UnknownModelException(
                requestedModel: "gpt-9", resolvedModel: "gpt-9",
                appliedLocation: null, appliedLocationIndex: null,
                knownProfiles: ["gpt-5.3-codex"]));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request_error",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CodexBadRequest_Returns400_WithAnthropicErrorEnvelope()
    {
        var (status, body) = await Invoke(ValidRequest, _ =>
            throw new CodexBadRequestException("malformed tool arguments for call_1"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request_error",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains("call_1", doc.RootElement.GetProperty("error").GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task TransientUpstream_Returns502()
    {
        var (status, body) = await Invoke(ValidRequest, _ =>
            throw new HttpRequestException("connection reset"));

        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Contains("upstream", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedException_Returns502()
    {
        var (status, _) = await Invoke(ValidRequest, _ =>
            throw new InvalidOperationException("something broke"));
        Assert.Equal(StatusCodes.Status502BadGateway, status);
    }

    // ── happy buffered path writes the upstream body through ─────────────────

    [Fact]
    public async Task BufferedSuccess_WritesBodyThrough_WithStatus()
    {
        const string upstreamJson = """{"type":"response","id":"r","status":"completed"}""";
        var (status, body) = await Invoke(ValidRequest, ctx =>
        {
            ctx.Target = new RouteTarget(BackendVendor.CopilotResponses, "/responses", "gpt-5.3-codex");
            ctx.Response.Status = StatusCodes.Status200OK;
            ctx.Response.Mode = ResponseMode.Buffered;
            ctx.Response.BufferedBody = Encoding.UTF8.GetBytes(upstreamJson);
            ctx.Response.Headers["Content-Type"] = "application/json";
        });

        Assert.Equal(StatusCodes.Status200OK, status);
        // T4's buffered path is passthrough → the Responses JSON reaches the client verbatim.
        Assert.Equal(upstreamJson, body);
    }

    [Fact]
    public async Task BufferedUpstreamError_PropagatesStatusAndBody()
    {
        // A 4xx from Copilot is buffered (error envelope) and passed through with
        // its status — the endpoint must not mask it as 200.
        const string errJson = """{"error":{"message":"bad effort"}}""";
        var (status, body) = await Invoke(ValidRequest, ctx =>
        {
            ctx.Target = new RouteTarget(BackendVendor.CopilotResponses, "/responses", "gpt-5.3-codex");
            ctx.Response.Status = StatusCodes.Status400BadRequest;
            ctx.Response.Mode = ResponseMode.Buffered;
            ctx.Response.BufferedBody = Encoding.UTF8.GetBytes(errJson);
            ctx.Response.Headers["Content-Type"] = "application/json";
        });

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(errJson, body);
    }
}

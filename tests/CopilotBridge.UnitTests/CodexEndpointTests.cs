using System.Net.ServerSentEvents;
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
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
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
            Microsoft.Extensions.Options.Options.Create(new CopilotBridge.Cli.Hosting.Options.TracingOptions()),
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

    // ── streaming success surfaces Copilot's cache tokens end-to-end ──────────
    // The strongest offline proof the Codex path WORKS: simulate Copilot's
    // /responses SSE for a near-total cache hit, run it through the REAL T3 to
    // build the IR stream the strategy hands the endpoint, drive the REAL
    // HandleAsync (which runs the REAL T4 + SSE relay), and assert the bytes the
    // client receives carry cached_tokens. Only the live HTTP hop is stubbed —
    // CodexPromptCacheHeadlessTests covers that against the real backend.

    [Fact]
    public async Task StreamingSuccess_SurfacesCopilotCacheTokensToClient()
    {
        // Mirrors production trace 0311: input 109589 of which 109440 were cached.
        var ir = RunT3OverCopilotSse(input: 109589, cached: 109440, output: 315, reasoning: 0);

        var (status, body) = await Invoke(ValidRequest, ctx =>
        {
            ctx.Target = new RouteTarget(BackendVendor.CopilotResponses, "/responses", "gpt-5.5");
            ctx.Response.Status = StatusCodes.Status200OK;
            ctx.Response.Mode = ResponseMode.Streaming;
            ctx.Response.EventStream = ToAsync(ir);
        });

        Assert.Equal(StatusCodes.Status200OK, status);
        var u = UsageFromCompletedEvent(body);
        Assert.Equal(109589, u.Input);
        Assert.Equal(109440, u.Cached);   // the cache hit reached the client through HandleAsync
        Assert.Equal(315, u.Output);
        Assert.Equal(109904, u.Total);    // input + output; cached is a subset, not added
    }

    /// <summary>
    /// Build a minimal Copilot <c>/responses</c> SSE for a cache-hit turn and run
    /// it through the REAL T3 (<see cref="ResponsesToAnthropicStream"/>), returning
    /// the IR stream the Codex strategy would hand the endpoint.
    /// </summary>
    private static List<SseItem<string>> RunT3OverCopilotSse(long input, long cached, long output, long reasoning)
    {
        var usage =
            $"{{\"input_tokens\":{input},\"input_tokens_details\":{{\"cached_tokens\":{cached}}},"
            + $"\"output_tokens\":{output},\"output_tokens_details\":{{\"reasoning_tokens\":{reasoning}}},"
            + $"\"total_tokens\":{input + output}}}";
        var copilot = new List<SseItem<string>>
        {
            new("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
            new($"{{\"type\":\"response.completed\",\"response\":{{\"id\":\"r\",\"status\":\"completed\",\"usage\":{usage}}}}}",
                "response.completed"),
        };
        var t3 = new ResponsesToAnthropicStream("gpt-5.5");
        var ir = new List<SseItem<string>>();
        foreach (var e in copilot) ir.AddRange(t3.Translate(e));
        ir.AddRange(t3.Flush());
        return ir;
    }

    private static async IAsyncEnumerable<SseItem<string>> ToAsync(IEnumerable<SseItem<string>> items)
    {
        foreach (var i in items) yield return i;
        await Task.CompletedTask;
    }

    /// <summary>Parse the endpoint's raw SSE output and read usage off response.completed.</summary>
    private static (long Input, long Cached, long Output, long Total) UsageFromCompletedEvent(string sseBody)
    {
        foreach (var block in sseBody.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var data = string.Join("\n", block.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l[5..].TrimStart()));
            if (data.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "response.completed") continue;
                var usage = root.GetProperty("response").GetProperty("usage");
                long S(string p) => usage.TryGetProperty(p, out var v) && v.TryGetInt64(out var n) ? n : 0;
                long N(string p, string c) => usage.TryGetProperty(p, out var d) && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty(c, out var v) && v.TryGetInt64(out var n) ? n : 0;
                return (S("input_tokens"), N("input_tokens_details", "cached_tokens"), S("output_tokens"), S("total_tokens"));
            }
        }
        throw new Xunit.Sdk.XunitException("no response.completed event in endpoint SSE output");
    }
}

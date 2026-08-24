using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Endpoints.Codex;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.Playground;

[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexContextRecoveryContractTests
{
    [Fact]
    public async Task MinimizedProductionCompactionCapture_EmitsRecoverableNativeFailure()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath()));
        var root = fixture.RootElement;
        var requestJson = root.GetProperty("request").GetRawText();
        var upstream = root.GetProperty("upstream_response");
        var upstreamBytes = Encoding.UTF8.GetBytes(upstream.GetProperty("body").GetRawText());

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/codex/responses";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var responseStream = new MemoryStream();
        http.Response.Body = responseStream;

        var bridge = new BridgeContext<MessagesRequest>();
        await CodexResponsesEndpoint.HandleAsync(
            http,
            bridge,
            new StubRunner(bridge, context =>
            {
                context.Target = new RouteTarget(
                    BackendVendor.CopilotResponses, "/responses", "gpt-5.6-sol");
                context.Response.Status = upstream.GetProperty("status").GetInt32();
                context.Response.Mode = ResponseMode.Buffered;
                context.Response.BufferedBody = upstreamBytes;
                context.Response.RawUpstreamResponseBody = upstreamBytes;
                context.Response.Headers["Content-Type"] =
                    upstream.GetProperty("content_type").GetString()!;
                context.Response.Headers["Content-Length"] = upstreamBytes.Length.ToString();
            }),
            DummyPipeline,
            new ResponsesToIrInboundAdapter(
                NullLogger<ResponsesToIrInboundAdapter>.Instance),
            new IrToResponsesOutboundAdapter(
                bridge, NullLogger<IrToResponsesOutboundAdapter>.Instance),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            new RequestAudit(
                Options.Create(new TracingOptions { Enabled = false }),
                NullLogger<MessagesRequest>.Instance),
            NullLogger<CodexResponsesEndpointTag>.Instance);

        var body = Encoding.UTF8.GetString(responseStream.ToArray());
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal("text/event-stream", http.Response.ContentType);
        Assert.Equal(1, body.Split("event: response.failed", StringSplitOptions.None).Length - 1);
        Assert.Contains("\"code\":\"context_length_exceeded\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("response.completed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("response.incomplete", body, StringComparison.Ordinal);
    }

    private sealed class StubRunner(
        BridgeContext<MessagesRequest> context,
        Action<BridgeContext<MessagesRequest>> behavior)
        : IPipelineRunner<MessagesRequest>
    {
        public Task RunAsync(Pipeline<MessagesRequest> pipeline)
        {
            behavior(context);
            return Task.CompletedTask;
        }
    }

    private static readonly Pipeline<MessagesRequest> DummyPipeline = new()
    {
        Name = "context-recovery-contract",
        RequestStages = [],
        ResponseStages = [],
        Strategies = new StrategyRegistry<MessagesRequest>([]),
    };

    private static string FixturePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "CopilotBridge.Playground",
                "Fixtures",
                "codex-context-compaction-minimized.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "Could not locate codex-context-compaction-minimized.json.");
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>Endpoint-level contract for the cross-routed count path: shared
/// routing/T2, exactly one count call, strict response parsing, and calibrated
/// downstream accounting.</summary>
public sealed class CountTokensResponsesEndpointTests
{
    private sealed class StubAuth : IAuthService
    {
        public bool IsAuthenticated => true;
        public string TokenLocation => "test";
        public string? CopilotApiBaseUrl => "https://api.test.githubcopilot.com";
        public DateTimeOffset? CopilotTokenExpiry => DateTimeOffset.MaxValue;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) => new("gh");
        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotAuthLease? rejectedLease = null, CancellationToken ct = default) =>
            new(new CopilotAuthLease
            {
                Token = "copilot", ApiBaseUrl = CopilotApiBaseUrl!,
                RefreshAt = DateTimeOffset.MaxValue, ServerExpiresAt = DateTimeOffset.MaxValue, Generation = 1,
            });
        public void SignOut() { }
    }

    private sealed class RecordingClient(byte[] response, HttpStatusCode status = HttpStatusCode.OK)
        : ICopilotClient
    {
        public int CountCalls { get; private set; }
        public int ResponsesCalls { get; private set; }
        public byte[]? CountBody { get; private set; }

        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default)
        {
            CountCalls++;
            CountBody = body.ToArray();
            var result = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(response),
            };
            result.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            return new(result);
        }

        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default)
        {
            ResponsesCalls++;
            throw new InvalidOperationException("count_tokens must never generate");
        }

        public ValueTask<HttpResponseMessage> PostMessagesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            IReadOnlyList<string>? anthropicBeta = null,
            IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed record Outcome(
        int Status, string Body, RecordingClient Client,
        IReadOnlyList<RecordedEvent> Logs);

    private static async Task<Outcome> Run(
        string request,
        byte[]? upstreamResponse = null,
        IReadOnlyDictionary<string, string>? headers = null,
        RoutesConfig? routes = null,
        CcToResponsesOptions? ccOptions = null)
    {
        routes ??= new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression { Model = "claude-opus-5" },
                    Use = new LocationUse { Model = "gpt-5.6-sol" },
                },
            ],
        };
        var client = new RecordingClient(
            upstreamResponse ?? Encoding.UTF8.GetBytes("{\"input_tokens\":1000}"));
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages/count_tokens";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        if (headers is not null)
            foreach (var (name, value) in headers) http.Request.Headers[name] = value;
        var response = new MemoryStream();
        http.Response.Body = response;

        await ClaudeCodeCountTokensEndpoint.HandleAsync(
            http, client, new StubAuth(),
            new RequestSummaryLogger(loggerFactory.CreateLogger<RequestSummaryLogger>()),
            TestAudit.Create(false),
            CountTokensTestServices.Planner(routes, loggerFactory),
            CountTokensTestServices.Context(),
            new CodexModelProfileCatalog(),
            CountTokensTestServices.Estimator(loggerFactory),
            Microsoft.Extensions.Options.Options.Create(
                ccOptions ?? new CcToResponsesOptions()));

        return new Outcome(
            http.Response.StatusCode,
            Encoding.UTF8.GetString(response.ToArray()),
            client,
            recorder.Events);
    }

    [Fact]
    public async Task RoutedCount_BuildsCountInputT2_Once_WithoutGeneration()
    {
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"count me\"}],"
            + "\"tools\":[{\"name\":\"Bash\",\"description\":\"run\",\"input_schema\":{\"type\":\"object\"}}]}";

        var outcome = await Run(request);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(1, outcome.Client.CountCalls);
        Assert.Equal(0, outcome.Client.ResponsesCalls);
        using var upstream = JsonDocument.Parse(outcome.Client.CountBody!);
        var root = upstream.RootElement;
        Assert.Equal("gpt-5.6-sol", root.GetProperty("model").GetString());
        Assert.True(root.TryGetProperty("input", out _));
        Assert.True(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("instructions", out _));
        Assert.False(root.TryGetProperty("max_output_tokens", out _));
        Assert.False(root.TryGetProperty("stream", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));

        using var downstream = JsonDocument.Parse(outcome.Body);
        Assert.True(downstream.RootElement.GetProperty("input_tokens").GetInt32() > 1000);
        var summary = outcome.Logs.Single(e =>
            e.Properties.TryGetValue("Kind", out var kind)
            && Equals(kind, "count_tokens"));
        Assert.Equal("1000", summary.Properties["RawCountInputTokens"]);
        Assert.Equal("gpt-5.6-sol", summary.Properties["ResolvedModel"]);
    }

    [Fact]
    public async Task RoutedCount_BytesEqualDirectSharedT2BuildOfTheCountShape()
    {
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":["
            + "{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"thinking\",\"thinking\":\"private scratch\",\"signature\":\"sig\"},"
            + "{\"type\":\"text\",\"text\":\"visible answer\"}]},"
            + "{\"role\":\"user\",\"content\":\"continue\"}],"
            + "\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":1024},"
            + "\"tools\":[{\"name\":\"Bash\",\"description\":\"run\","
            + "\"input_schema\":{\"type\":\"object\"}}]}";

        var outcome = await Run(request);

        Assert.True(ResponsesCountRequest.TryParse(
            Encoding.UTF8.GetBytes(request), out var parsed, out var error), error);
        var directInput = parsed! with { Model = "gpt-5.6-sol" };
        var expected = ResponsesRequestBuilder.Build(
            directInput, new CodexModelProfileCatalog()).Body;
        Assert.Equal(expected, outcome.Client.CountBody);

        var wire = Encoding.UTF8.GetString(outcome.Client.CountBody!);
        Assert.Contains("visible answer", wire);
        Assert.DoesNotContain("private scratch", wire);
        using var doc = JsonDocument.Parse(outcome.Client.CountBody!);
        Assert.False(doc.RootElement.TryGetProperty("reasoning", out _));
        Assert.False(doc.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.False(doc.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task SubagentCount_UsesTheSameRecursiveAgentToolFilter_WithoutChangingSource()
    {
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],"
            + "\"tools\":["
            + "{\"name\":\"Agent\",\"input_schema\":{\"type\":\"object\"}},"
            + "{\"name\":\"Bash\",\"input_schema\":{\"type\":\"object\"}}]}";

        var outcome = await Run(
            request,
            headers: new Dictionary<string, string>
            {
                ["x-claude-code-agent-id"] = "child-1",
            });

        using var wire = JsonDocument.Parse(outcome.Client.CountBody!);
        var names = wire.RootElement.GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["Bash"], names);
        Assert.Contains("\"name\":\"Agent\"", request);
    }

    [Fact]
    public async Task FirstMatchingLocationWins_AndDoesNotChain()
    {
        var routes = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression { Model = "claude-opus-5" },
                    Use = new LocationUse { Model = "gpt-5.6-sol" },
                },
                new RouteLocation
                {
                    When = new MatchExpression { Model = "gpt-5.6-sol" },
                    Use = new LocationUse { Model = "claude-sonnet-4.6" },
                },
            ],
        };
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}";

        var outcome = await Run(request, routes: routes);

        using var upstream = JsonDocument.Parse(outcome.Client.CountBody!);
        Assert.Equal("gpt-5.6-sol", upstream.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task MissingOptionalEffort_DoesNotMatchAnEffortLocation()
    {
        var routes = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression
                    {
                        Model = "claude-opus-5",
                        Effort = "max",
                    },
                    Use = new LocationUse { Model = "gpt-5.6-sol" },
                },
            ],
        };
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}";

        var outcome = await Run(request, routes: routes);

        Assert.Equal(request, Encoding.UTF8.GetString(outcome.Client.CountBody!));
    }

    [Fact]
    public async Task UnknownTokenBearingField_FailsBeforeAnyUpstreamCall()
    {
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"future_token_field\":{\"value\":9}}";

        var outcome = await Run(request);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(0, outcome.Client.CountCalls);
        Assert.Contains("unsupported cross-routed count_tokens field", outcome.Body);
    }

    [Fact]
    public async Task UnsupportedNestedOutputConfig_FailsInsteadOfSilentlyDroppingIt()
    {
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],"
            + "\"output_config\":{\"effort\":\"high\",\"task_budget\":{\"type\":\"tokens\",\"total\":4096}}}";

        var outcome = await Run(request);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(0, outcome.Client.CountCalls);
        Assert.Contains("output_config.task_budget", outcome.Body);
    }

    [Theory]
    [InlineData("{\"role\":\"user\",\"content\":\"x\",\"future_budget\":4096}", "messages[0].future_budget")]
    [InlineData("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"x\",\"future_budget\":4096}]}", "messages[0].content[0].future_budget")]
    public async Task UnsupportedNestedMessageField_FailsBeforeAnyUpstreamCall(
        string message, string expectedPath)
    {
        var request = "{\"model\":\"claude-opus-5\",\"messages\":[" + message + "]}";

        var outcome = await Run(request);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(0, outcome.Client.CountCalls);
        Assert.Contains(expectedPath, outcome.Body);
    }

    [Fact]
    public async Task UnsupportedToolField_FailsButOpaqueSchemaPropertiesAndInputRemainAllowed()
    {
        const string unsupported =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],"
            + "\"tools\":[{\"name\":\"Bash\",\"future_budget\":4096,"
            + "\"input_schema\":{\"type\":\"object\"}}]}";
        var rejected = await Run(unsupported);
        Assert.Equal(400, rejected.Status);
        Assert.Equal(0, rejected.Client.CountCalls);
        Assert.Contains("tools[0].future_budget", rejected.Body);

        const string unsupportedSchema =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],"
            + "\"tools\":[{\"name\":\"Bash\",\"input_schema\":{\"type\":\"object\","
            + "\"additionalProperties\":false}}]}";
        var schemaRejected = await Run(unsupportedSchema);
        Assert.Equal(400, schemaRejected.Status);
        Assert.Equal(0, schemaRejected.Client.CountCalls);
        Assert.Contains("tools[0].input_schema.additionalProperties", schemaRejected.Body);

        const string opaqueJson =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"tool_use\",\"id\":\"call-1\",\"name\":\"Bash\","
            + "\"input\":{\"future_nested\":{\"budget\":4096}}}]}],"
            + "\"tools\":[{\"name\":\"Bash\",\"input_schema\":{\"type\":\"object\","
            + "\"properties\":{\"future_nested\":{\"type\":\"object\",\"x-vendor-budget\":4096}}}}]}";
        var accepted = await Run(opaqueJson);
        Assert.Equal(200, accepted.Status);
        Assert.Equal(1, accepted.Client.CountCalls);
        Assert.Contains("future_nested", Encoding.UTF8.GetString(accepted.Client.CountBody!));
    }

    [Theory]
    [InlineData(
        "{\"type\":\"tool_result\",\"tool_use_id\":\"call-1\",\"content\":[{\"type\":\"text\",\"text\":\"x\",\"future_budget\":4096}]}",
        "messages[0].content[0].content[0].future_budget")]
    [InlineData(
        "{\"type\":\"document\",\"source\":{\"type\":\"text\",\"data\":\"count me\"}}",
        "type=document")]
    [InlineData(
        "{\"type\":\"image\",\"source\":{\"type\":\"file\",\"file_id\":\"file-1\"}}",
        "source.type=file")]
    public async Task NestedContentThatT2CannotPreserve_FailsExplicitly(
        string block, string expectedPath)
    {
        var request = "{\"model\":\"claude-opus-5\",\"messages\":["
            + "{\"role\":\"user\",\"content\":[" + block + "]}]}";

        var outcome = await Run(request);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(0, outcome.Client.CountCalls);
        Assert.Contains(expectedPath, outcome.Body);
    }

    [Fact]
    public async Task TransformedCountFailure_IsRelayedWithoutSourceCountFallback()
    {
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}";
        var client = new RecordingClient(
            Encoding.UTF8.GetBytes("target-count-rejected"),
            HttpStatusCode.UnprocessableEntity);
        var routes = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression { Model = "claude-opus-5" },
                    Use = new LocationUse { Model = "gpt-5.6-sol" },
                },
            ],
        };
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages/count_tokens";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        http.Response.Body = new MemoryStream();

        await ClaudeCodeCountTokensEndpoint.HandleAsync(
            http, client, new StubAuth(),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            TestAudit.Create(false), CountTokensTestServices.Planner(routes),
            CountTokensTestServices.Context(), new CodexModelProfileCatalog(),
            CountTokensTestServices.Estimator(), CountTokensTestServices.CcOptions());

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, http.Response.StatusCode);
        Assert.Equal(1, client.CountCalls);
        Assert.Equal(0, client.ResponsesCalls);
        Assert.Equal("target-count-rejected",
            Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()));
    }

    [Fact]
    public async Task InvalidSuccessfulUpstreamCount_FailsExplicitly()
    {
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}";

        var outcome = await Run(request, Encoding.UTF8.GetBytes("{\"input_tokens\":-1}"));

        Assert.Equal(502, outcome.Status);
        Assert.Contains("invalid upstream count_tokens response", outcome.Body);
        Assert.Equal(1, outcome.Client.CountCalls);
    }

    [Fact]
    public async Task BetaLocationMatch_UsesActualSdkHeader()
    {
        var routes = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression
                    {
                        Model = "claude-opus-5",
                        Header = new HeaderMatch
                        {
                            Name = "anthropic-beta",
                            Contains = "token-counting-2024-11-01",
                        },
                    },
                    Use = new LocationUse { Model = "gpt-5.6-sol" },
                },
            ],
        };
        const string request =
            "{\"model\":\"claude-opus-5\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}";
        var client = new RecordingClient(Encoding.UTF8.GetBytes("{\"input_tokens\":8}"));
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages/count_tokens";
        http.Request.Headers["anthropic-beta"] = "token-counting-2024-11-01";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        http.Response.Body = new MemoryStream();

        await ClaudeCodeCountTokensEndpoint.HandleAsync(
            http, client, new StubAuth(),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            TestAudit.Create(false), CountTokensTestServices.Planner(routes),
            CountTokensTestServices.Context(), new CodexModelProfileCatalog(),
            CountTokensTestServices.Estimator(), CountTokensTestServices.CcOptions());

        using var upstream = JsonDocument.Parse(client.CountBody!);
        Assert.Equal("gpt-5.6-sol", upstream.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task EffortLocationMatch_UsesCountRequestsOwnOptionalEffort()
    {
        var routes = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression
                    {
                        Model = "claude-opus-5",
                        Effort = "max",
                    },
                    Use = new LocationUse
                    {
                        Model = "gpt-5.6-sol",
                        EffortMap = new() { ["max"] = "xhigh" },
                    },
                },
            ],
        };
        const string request =
            "{\"model\":\"claude-opus-5\",\"output_config\":{\"effort\":\"max\"},"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}";
        var client = new RecordingClient(Encoding.UTF8.GetBytes("{\"input_tokens\":8}"));
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages/count_tokens";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        http.Response.Body = new MemoryStream();

        await ClaudeCodeCountTokensEndpoint.HandleAsync(
            http, client, new StubAuth(),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            TestAudit.Create(false), CountTokensTestServices.Planner(routes),
            CountTokensTestServices.Context(), new CodexModelProfileCatalog(),
            CountTokensTestServices.Estimator(), CountTokensTestServices.CcOptions());

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        using var upstream = JsonDocument.Parse(client.CountBody!);
        Assert.Equal("gpt-5.6-sol", upstream.RootElement.GetProperty("model").GetString());
        Assert.Equal("xhigh",
            upstream.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }
}

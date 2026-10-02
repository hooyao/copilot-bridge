using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Operator-visible effort diagnostics must name the actual wire constraint.
/// The backend accepts MAI minimal alone but rejects it with web_search; a
/// rejected effort instead uses the profile's independent default.
/// </summary>
public sealed class CodexEffortDiagnosticTests
{
    [Fact]
    public async Task MinimalWithWebSearch_LogsCombinationAndCompatibleEffort()
    {
        var result = await RunAsync("minimal", includeWebSearch: true);

        Assert.Equal("low", result.Wire["reasoning"]?["effort"]?.GetValue<string>());
        Assert.Equal("web_search", result.Wire["tools"]?[0]?["type"]?.GetValue<string>());
        Assert.Contains("profile.effort.web_search", result.MutationCodes);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("accepted by model 'mai-code-1.1-flash' alone but not with web_search", warning.Message);
        Assert.Contains("compatible effort 'low'", warning.Message);
        Assert.DoesNotContain("not accepted by model", warning.Message);
        Assert.DoesNotContain("default", warning.Message);
        Assert.Equal("low", warning.Properties["Outbound"]);
    }

    [Fact]
    public async Task MinimalWithoutWebSearch_IsPreservedWithoutWarning()
    {
        var result = await RunAsync("minimal", includeWebSearch: false);

        Assert.Equal("minimal", result.Wire["reasoning"]?["effort"]?.GetValue<string>());
        Assert.Empty(result.Warnings);
        Assert.DoesNotContain("profile.effort", result.MutationCodes);
    }

    [Fact]
    public async Task RejectedEffort_LogsProfileDefaultWithoutWebSearchReason()
    {
        var result = await RunAsync("none", includeWebSearch: false);

        Assert.Equal("high", result.Wire["reasoning"]?["effort"]?.GetValue<string>());
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("effort 'none' not accepted by model 'mai-code-1.1-flash'", warning.Message);
        Assert.Contains("profile default 'high'", warning.Message);
        Assert.DoesNotContain("with web_search", warning.Message);
        Assert.DoesNotContain("profile.effort.web_search", result.MutationCodes);
        Assert.Equal("high", warning.Properties["Default"]);
    }

    private static async Task<Result> RunAsync(string effort, bool includeWebSearch)
    {
        ProviderExtensions? extensions = null;
        if (includeWebSearch)
        {
            using var doc = JsonDocument.Parse("""{"tools":[{"type":"web_search"}]}""");
            extensions = new ProviderExtensions
            {
                ByProvider = new Dictionary<string, JsonElement>
                {
                    ["openai"] = doc.RootElement.Clone(),
                },
            };
        }

        var ctx = new BridgeContext<MessagesRequest>
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/codex/responses",
                Body = new MessagesRequest
                {
                    Model = "mai-code-1.1-flash",
                    MaxTokens = 1024,
                    Messages = [new MessageParam
                    {
                        Role = Role.User,
                        Content = [new TextBlockParam { Text = "probe" }],
                    }],
                    OutputConfig = new OutputConfig { Effort = effort },
                    ProviderExtensions = extensions,
                },
            },
            Response = new BridgeResponse(),
        };
        var client = new CapturingClient();
        var logs = new RecordingLoggerProvider();
        using var logFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var strategy = new CopilotResponsesStrategy(
            client,
            new CodexModelProfileCatalog(),
            ctx,
            TestAudit.Create(false),
            Options.Create(new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 0,
            }),
            logFactory.CreateLogger<CopilotResponsesStrategy>());

        await strategy.ForwardAsync();

        var wire = JsonNode.Parse(client.LastBody!)?.AsObject()
            ?? throw new InvalidDataException("Strategy did not send a Responses body.");
        return new Result(
            wire,
            logs.Events.Where(entry => entry.Level == LogLevel.Warning).ToArray(),
            ctx.Response.RequestMutationCodes ?? "");
    }

    private sealed record Result(
        JsonObject Wire,
        IReadOnlyList<RecordedEvent> Warnings,
        string MutationCodes);

    private sealed class CapturingClient : ICopilotClient
    {
        public byte[]? LastBody { get; private set; }

        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default)
        {
            LastBody = body.ToArray();
            return new(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("test stop")),
            });
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
}

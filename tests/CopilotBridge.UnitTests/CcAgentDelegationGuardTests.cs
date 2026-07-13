using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: a Claude Code sub-agent routed to a Responses backend must not be
/// offered the recursive <c>Agent</c> delegation primitive when the default-on
/// compatibility guard is enabled. Root delegation, opt-out behavior, native
/// Codex tool bags, sibling tools, and the original IR remain observable exactly
/// as required by the change spec.
/// </summary>
public class CcAgentDelegationGuardTests
{
    private static readonly CodexModelProfileCatalog Catalog = new();

    private static MessagesRequest ClaudeIr(ToolChoice? choice = null) => new()
    {
        Model = "gpt-5.6-sol",
        MaxTokens = 1024,
        Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "inspect" }] }],
        Tools =
        [
            new Tool { Name = "Agent", Description = "Delegate work.", InputSchema = new InputSchema() },
            new Tool { Name = "Bash", Description = "Run a command.", InputSchema = new InputSchema() },
        ],
        ToolChoice = choice,
        Stream = false,
    };

    private static JsonObject Emit(MessagesRequest ir, bool filter) =>
        JsonNode.Parse(ResponsesRequestBuilder.Build(ir, Catalog, filter).Body)!.AsObject();

    private static string[] ToolNames(JsonObject wire) =>
        wire["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();

    [Fact]
    public void EnabledSubagent_RemovesOnlyAgent_WithoutMutatingIr()
    {
        var ir = ClaudeIr();

        var wire = Emit(ir, filter: true);

        Assert.Equal(["Bash"], ToolNames(wire));
        Assert.Equal(["Agent", "Bash"], ir.Tools!.Select(t => t.Name));
    }

    [Fact]
    public void RootOrDisabledSubagent_RetainsAgent()
    {
        var wire = Emit(ClaudeIr(), filter: false);

        Assert.Equal(["Agent", "Bash"], ToolNames(wire));
    }

    [Fact]
    public void ForcedRemovedAgent_DowngradesToAuto()
    {
        var wire = Emit(ClaudeIr(new ToolChoiceTool { Name = "Agent" }), filter: true);

        Assert.Equal("auto", wire["tool_choice"]!.GetValue<string>());
        Assert.DoesNotContain("Agent", ToolNames(wire));
    }

    [Fact]
    public void ForcedSurvivingTool_RemainsForced()
    {
        var wire = Emit(ClaudeIr(new ToolChoiceTool { Name = "Bash" }), filter: true);

        Assert.Equal("function", wire["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("Bash", wire["tool_choice"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void NativeCodexBag_NamedAgent_IsUntouched()
    {
        using var doc = JsonDocument.Parse(
            """{"tools":[{"type":"function","name":"Agent","parameters":{"type":"object"}}]}""");
        var ir = ClaudeIr() with
        {
            Tools = null,
            ProviderExtensions = new ProviderExtensions
            {
                ByProvider = new Dictionary<string, JsonElement> { ["openai"] = doc.RootElement.Clone() },
            },
        };

        var wire = Emit(ir, filter: true);

        Assert.Equal(["Agent"], ToolNames(wire));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData("   ", "parent", false)]
    [InlineData(null, "parent", false)]
    [InlineData("child", null, true)]
    [InlineData("child", "parent", true)]
    public void SubagentClassification_UsesNonEmptyAgentId_NotParent(
        string? agentId, string? parentId, bool expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (agentId is not null) headers["x-claude-code-agent-id"] = agentId;
        if (parentId is not null) headers["x-claude-code-parent-agent-id"] = parentId;

        Assert.Equal(expected, ClaudeCodeMessagesEndpoint.IsClaudeCodeSubagent(headers));
    }

    [Fact]
    public void Options_DefaultOn_AndExplicitFalseBinds()
    {
        Assert.True(ResolveOptions(new ConfigurationBuilder().Build()).PreventRecursiveAgentDelegation);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:CcToResponses:PreventRecursiveAgentDelegation"] = "false",
        }).Build();
        Assert.False(ResolveOptions(config).PreventRecursiveAgentDelegation);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task Strategy_CombinesConfigurationAndRequestScope(
        bool enabled, bool isSubagent, bool expectAgent)
    {
        var ctx = new BridgeContext<MessagesRequest>
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/cc/v1/messages",
                Body = ClaudeIr(),
            },
            Response = new BridgeResponse(),
            IsClaudeCodeSubagent = isSubagent,
        };
        var client = new CapturingClient();
        var strategy = new CopilotResponsesStrategy(
            client,
            Catalog,
            ctx,
            TestAudit.Create(false),
            Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0, StreamIdleTimeoutSeconds = 0 }),
            NullLogger<CopilotResponsesStrategy>.Instance,
            Options.Create(new CcToResponsesOptions { PreventRecursiveAgentDelegation = enabled }));

        await strategy.ForwardAsync();

        var wire = JsonNode.Parse(client.LastBody!)!.AsObject();
        Assert.Equal(expectAgent, ToolNames(wire).Contains("Agent"));
        Assert.Contains("Bash", ToolNames(wire));
    }

    private static CcToResponsesOptions ResolveOptions(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.Configure<CcToResponsesOptions>(config.GetSection("Pipeline:CcToResponses"));
        return services.BuildServiceProvider().GetRequiredService<IOptions<CcToResponsesOptions>>().Value;
    }

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

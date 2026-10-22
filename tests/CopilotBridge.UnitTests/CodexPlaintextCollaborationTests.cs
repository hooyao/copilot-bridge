using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using CopilotBridge.UnitTests.Invariant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: an explicitly enabled native stateless request must obtain complete
/// plaintext tasks and select Codex's plaintext dispatcher, while every unrelated
/// field and historical encrypted task remains unchanged. No assertions depend on
/// how the adapter stores or traverses its request-local data.
/// </summary>
public sealed class CodexPlaintextCollaborationTests
{
    private const string NamespaceJson = """
        {"type":"namespace","name":"collaboration","description":"native tools","future":{"keep":true},"tools":[
          {"type":"function","name":"spawn_agent","parameters":{"type":"object","properties":{"message":{"type":"string","encrypted":true,"description":"complete task"},"task_name":{"type":"string"}},"required":["task_name","message"],"additionalProperties":false},"strict":false},
          {"type":"function","name":"send_message","parameters":{"type":"object","properties":{"message":{"type":"string","encrypted":true}}}},
          {"type":"function","name":"followup_task","parameters":{"type":"object","properties":{"message":{"type":"string","encrypted":true}}}},
          {"type":"function","name":"wait_agent","parameters":{"type":"object","properties":{"target":{"type":"string"}}}}
        ]}
        """;

    private static JsonObject Request(string inventory = "tools")
    {
        var body = JsonNode.Parse("""
            {"model":"gpt-6-astra","stream":true,"store":false,"reasoning":{"effort":"low"},
             "input":[{"type":"agent_message","author":"/root","recipient":"/root/old","content":[{"type":"encrypted_content","encrypted_content":"old-real-ciphertext"}]},
                      {"type":"reasoning","encrypted_content":"reasoning-ciphertext","summary":[]}],
             "tools":[],"future":{"untouched":[1,null,true]}}
            """)!.AsObject();
        if (inventory == "tools") body["tools"]!.AsArray().Add(JsonNode.Parse(NamespaceJson));
        else body["input"]!.AsArray().Add(new JsonObject
        {
            ["type"] = inventory, ["tools"] = new JsonArray(JsonNode.Parse(NamespaceJson)),
        });
        return body;
    }

    private static byte[] Bytes(JsonNode body) => Encoding.UTF8.GetBytes(body.ToJsonString());

    [Fact]
    public void DefaultIsOffAndExplicitConfigurationBinds()
    {
        Assert.False(new CodexPlaintextCollaborationOptions().Enabled);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Codex:PlaintextCollaboration:Enabled"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.Configure<CodexPlaintextCollaborationOptions>(config.GetSection("Codex:PlaintextCollaboration"));
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<IOptions<CodexPlaintextCollaborationOptions>>().Value.Enabled);
    }

    [Fact]
    public void CapturedCurrentClientInventoryProjectsThroughT1AndT2()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Codex", "plaintext-collaboration.json")))!;
        var native = fixture["body"]!;
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(native.ToJsonString()));
        var adapter = new CodexPlaintextCollaboration();
        var wire = JsonNode.Parse(adapter.PrepareRequest(CodexRoundTrip.ToResponsesWire(ir)))!;
        var inventory = wire["input"]![0]!;
        Assert.Equal("additional_tools", inventory["type"]!.GetValue<string>());
        var projected = inventory["tools"]![0]!;
        Assert.Equal("bridge_plaintext_collaboration", projected["name"]!.GetValue<string>());
        foreach (var tool in projected["tools"]!.AsArray().Where(item =>
            item!["name"]!.GetValue<string>() is "spawn_agent" or "send_message" or "followup_task"))
            Assert.False(tool!["parameters"]!["properties"]!["message"]!.AsObject().ContainsKey("encrypted"));
        Assert.True(JsonNode.DeepEquals(native["input"]![1], wire["input"]![1]));
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("additional_tools")]
    [InlineData("tool_search_output")]
    public void EveryInventoryProjectsOnlyMessageEncryptionAndPreservesHistory(string inventory)
    {
        var request = Request(inventory);
        request["store"] = true;
        var adapter = new CodexPlaintextCollaboration();
        var wire = JsonNode.Parse(adapter.PrepareRequest(Bytes(request)))!;
        var tool = inventory == "tools" ? wire["tools"]![0]!
            : wire["input"]![2]!["tools"]![0]!;
        Assert.Equal("bridge_plaintext_collaboration", tool["name"]!.GetValue<string>());
        foreach (var action in tool["tools"]!.AsArray().Take(3))
        {
            var message = action!["parameters"]!["properties"]!["message"]!.AsObject();
            Assert.False(message.ContainsKey("encrypted"));
            Assert.Equal("string", message["type"]!.GetValue<string>());
        }
        Assert.Equal("complete task", tool["tools"]![0]!["parameters"]!["properties"]!["message"]!["description"]!.GetValue<string>());
        Assert.Equal("wait_agent", tool["tools"]![3]!["name"]!.GetValue<string>());
        Assert.True(tool["future"]!["keep"]!.GetValue<bool>());
        Assert.False(wire["store"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(request["input"]![0], wire["input"]![0]));
        Assert.True(JsonNode.DeepEquals(request["input"]![1], wire["input"]![1]));
        Assert.True(JsonNode.DeepEquals(request["future"], wire["future"]));
    }

    [Fact]
    public void SimilarApplicationNamespaceAndUnrecognizedEncryptedActionAreInert()
    {
        var body = Request();
        body["tools"]![0]!["tools"]![0]!["name"] = "application_send";
        body["tools"]![0]!["tools"]!.AsArray().RemoveAt(2);
        body["tools"]![0]!["tools"]!.AsArray().RemoveAt(1);
        var original = Bytes(body);
        var adapter = new CodexPlaintextCollaboration();
        Assert.Same(original, adapter.PrepareRequest(original));
        Assert.False(adapter.Active);
    }

    [Theory]
    [InlineData("previous_response_id")]
    [InlineData("conversation")]
    public void StatefulContinuationFailsBeforeSending(string field)
    {
        var body = Request();
        body[field] = "stateful-id";
        Assert.Throws<CodexBadRequestException>(() => new CodexPlaintextCollaboration().PrepareRequest(Bytes(body)));
    }

    [Fact]
    public void ReservedNamespaceCollisionFailsExplicitly()
    {
        var body = Request();
        body["tools"]!.AsArray().Add(JsonNode.Parse("""{"type":"namespace","name":"bridge_plaintext_collaboration","tools":[]}"""));
        Assert.Throws<CodexBadRequestException>(() => new CodexPlaintextCollaboration().PrepareRequest(Bytes(body)));
    }

    [Theory]
    [InlineData("tools", ".")]
    [InlineData("tools", "__")]
    [InlineData("additional_tools", ".")]
    [InlineData("additional_tools", "__")]
    [InlineData("tool_search_output", ".")]
    [InlineData("tool_search_output", "__")]
    public void ReservedQualifiedFunctionCollisionFailsBeforeItCanBeMisrouted(string inventory, string separator)
    {
        var body = Request(inventory);
        var tools = inventory == "tools" ? body["tools"]!.AsArray()
            : body["input"]![2]!["tools"]!.AsArray();
        tools.Add(new JsonObject
        {
            ["type"] = "function", ["name"] = "bridge_plaintext_collaboration" + separator + "spawn_agent",
            ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{}}"""),
        });
        Assert.Throws<CodexBadRequestException>(() => new CodexPlaintextCollaboration().PrepareRequest(Bytes(body)));
    }

    [Theory]
    [InlineData(true, "namespace")]
    [InlineData(false, "namespace")]
    [InlineData(true, ".")]
    [InlineData(false, ".")]
    [InlineData(true, "__")]
    [InlineData(false, "__")]
    public void NamedFunctionOutputReplayProjectsIdentityWithoutChangingTheOutput(bool paired, string qualification)
    {
        var body = Request();
        var input = body["input"]!.AsArray();
        if (paired) input.Add(JsonNode.Parse("""{"type":"function_call","namespace":"collaboration","name":"spawn_agent","call_id":"paired_call","arguments":"{}"}"""));
        var toolOutput = new JsonObject
        {
            ["type"] = "function_call_output", ["id"] = "fco_preserved",
            ["name"] = qualification == "namespace" ? "spawn_agent" : "collaboration" + qualification + "spawn_agent",
            ["output"] = JsonNode.Parse("""[{"type":"input_text","text":"complete result 中文"}]"""),
            ["future_metadata"] = new JsonObject { ["retained"] = true },
        };
        if (qualification == "namespace") toolOutput["namespace"] = "collaboration";
        if (paired) toolOutput["call_id"] = "paired_call";
        input.Add(toolOutput);
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(body.ToJsonString()));
        var wire = JsonNode.Parse(new CodexPlaintextCollaboration().PrepareRequest(CodexRoundTrip.ToResponsesWire(ir)))!;
        var actual = wire["input"]!.AsArray().Single(item => item?["type"]?.GetValue<string>() == "function_call_output")!;
        Assert.Equal("bridge_plaintext_collaboration", actual["namespace"]?.GetValue<string>());
        Assert.Equal("spawn_agent", actual["name"]!.GetValue<string>());
        Assert.Equal("fco_preserved", actual["id"]!.GetValue<string>());
        Assert.Equal(paired, actual.AsObject().ContainsKey("call_id"));
        if (paired) Assert.Equal("paired_call", actual["call_id"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(toolOutput["output"], actual["output"]));
        Assert.True(actual["future_metadata"]!["retained"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("collaboration.spawn_agent", "bridge_plaintext_collaboration.spawn_agent")]
    [InlineData("collaboration__spawn_agent", "bridge_plaintext_collaboration__spawn_agent")]
    public void QualifiedChoicesAndSavedCallsUseTheSameNamespace(string clientName, string wireName)
    {
        var body = Request();
        body["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = clientName };
        const string arguments = "{\"message\":\"task with \\n and 中文\",\"task_name\":\"worker\"}";
        body["input"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "function_call", ["name"] = clientName, ["call_id"] = "call_keep", ["arguments"] = arguments,
        });
        var wire = JsonNode.Parse(new CodexPlaintextCollaboration().PrepareRequest(Bytes(body)))!;
        Assert.Equal(wireName, wire["tool_choice"]!["name"]!.GetValue<string>());
        Assert.Equal("bridge_plaintext_collaboration", wire["input"]![2]!["namespace"]!.GetValue<string>());
        Assert.Equal("spawn_agent", wire["input"]![2]!["name"]!.GetValue<string>());
        Assert.Equal(arguments, wire["input"]![2]!["arguments"]!.GetValue<string>());
        Assert.Equal("call_keep", wire["input"]![2]!["call_id"]!.GetValue<string>());
    }

    [Fact]
    public void AllowedChoicesPreserveTheirOtherFields()
    {
        var body = Request();
        body["tool_choice"] = JsonNode.Parse("""{"type":"allowed_tools","mode":"required","future":7,"tools":[{"type":"namespace","name":"collaboration"},{"type":"function","namespace":"other","name":"keep"}]}""");
        var wire = JsonNode.Parse(new CodexPlaintextCollaboration().PrepareRequest(Bytes(body)))!;
        Assert.Equal("bridge_plaintext_collaboration", wire["tool_choice"]!["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("other", wire["tool_choice"]!["tools"]![1]!["namespace"]!.GetValue<string>());
        Assert.Equal(7, wire["tool_choice"]!["future"]!.GetValue<int>());
    }

    [Fact]
    public void SparseStreamingCallAndTerminalSelectPlaintextDispatchWithCompleteArguments()
    {
        var adapter = ActiveAdapter();
        var added = Restore(adapter, """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","namespace":"bridge_plaintext_collaboration","name":"spawn_agent","call_id":"call_1","arguments":"","future":true}}""");
        AssertPlaintext(added["item"]!);
        var done = Restore(adapter, """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","namespace":null,"name":"","call_id":"call_1","arguments":"{\"message\":\"full task 中文\"}","future":17}}""");
        AssertPlaintext(done["item"]!);
        Assert.Equal("{\"message\":\"full task 中文\"}", done["item"]!["arguments"]!.GetValue<string>());
        Assert.Equal(17, done["item"]!["future"]!.GetValue<int>());
        var argsDone = Restore(adapter, """{"type":"response.function_call_arguments.done","output_index":0,"arguments":"complete"}""");
        AssertPlaintext(argsDone);
        var terminal = Restore(adapter, """{"type":"response.completed","response":{"output":[{"type":"function_call","arguments":"complete"}],"future":{"retained":true}}}""");
        AssertPlaintext(terminal["response"]!["output"]![0]!);
        Assert.True(terminal["response"]!["future"]!["retained"]!.GetValue<bool>());
    }

    [Fact]
    public void BufferedQualifiedIdentityRestoresAndNonMessageActionsDoNotAcquireMarker()
    {
        var adapter = ActiveAdapter();
        var wire = Encoding.UTF8.GetBytes("""{"object":"response","status":"completed","output":[{"type":"function_call","name":"bridge_plaintext_collaboration.send_message","arguments":"{\"message\":\"complete\"}"},{"type":"function_call","namespace":"bridge_plaintext_collaboration","name":"wait_agent","arguments":"{}"}],"future":23}""");
        var restored = JsonNode.Parse(adapter.RestoreResponse(wire))!;
        AssertPlaintext(restored["output"]![0]!, "send_message");
        Assert.Equal("collaboration", restored["output"]![1]!["namespace"]!.GetValue<string>());
        Assert.False(restored["output"]![1]!.AsObject().ContainsKey("encrypted_function_args"));
        Assert.Equal(23, restored["future"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("[\"message\"]")]
    [InlineData("null")]
    public void EncryptedMarkerOnProjectedPlaintextCallFailsClosed(string marker)
    {
        var adapter = ActiveAdapter();
        Assert.Throws<UpstreamResponseFailedException>(() => Restore(adapter,
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"namespace\":\"bridge_plaintext_collaboration\",\"name\":\"spawn_agent\",\"encrypted_function_args\":" + marker + "}}"));
    }

    [Fact]
    public void ConflictingIdentityFailsAndRequestStateDoesNotLeak()
    {
        var adapter = ActiveAdapter();
        Restore(adapter, """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","namespace":"bridge_plaintext_collaboration","name":"spawn_agent","call_id":"first"}}""");
        Assert.Throws<UpstreamResponseFailedException>(() => Restore(adapter,
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","name":"send_message","call_id":"first"}}"""));
        var other = ActiveAdapter();
        var fresh = Restore(other, """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","namespace":"bridge_plaintext_collaboration","name":"followup_task","call_id":"second"}}""");
        AssertPlaintext(fresh["item"]!, "followup_task");
    }

    [Theory]
    [InlineData(false, "/codex/responses", false)]
    [InlineData(true, "/cc/v1/messages", false)]
    [InlineData(true, "/codex/responses", true)]
    public async Task StrategyAppliesOnlyToExplicitlyEnabledNativeRequests(bool enabled, string path, bool projected)
    {
        var body = Request();
        body["stream"] = false;
        var context = new BridgeContext<MessagesRequest>
        {
            Response = new BridgeResponse(),
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST", Path = path,
                Body = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(body.ToJsonString())),
            },
        };
        var client = new CapturingClient();
        var strategy = new CopilotResponsesStrategy(client, new CodexModelProfileCatalog(), context,
            TestAudit.Create(false), Options.Create(new UpstreamTimeoutOptions()),
            NullLogger<CopilotResponsesStrategy>.Instance,
            collaborationOptions: Options.Create(new CodexPlaintextCollaborationOptions { Enabled = enabled }));
        await strategy.ForwardAsync();
        var sent = JsonNode.Parse(client.Sent!)!;
        Assert.Equal(projected ? "bridge_plaintext_collaboration" : "collaboration", sent["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(projected, context.Response.RequestMutationCodes.Contains("policy.plaintext_collaboration", StringComparison.Ordinal));
    }

    private static CodexPlaintextCollaboration ActiveAdapter()
    {
        var adapter = new CodexPlaintextCollaboration();
        adapter.PrepareRequest(Bytes(Request()));
        return adapter;
    }

    private static JsonNode Restore(CodexPlaintextCollaboration adapter, string json) =>
        JsonNode.Parse(adapter.RestoreEvent(new SseItem<string>(json, JsonNode.Parse(json)!["type"]!.GetValue<string>())).Data)!;

    private static void AssertPlaintext(JsonNode call, string action = "spawn_agent")
    {
        Assert.Equal("collaboration", call["namespace"]!.GetValue<string>());
        Assert.Equal(action, call["name"]!.GetValue<string>());
        Assert.Empty(call["encrypted_function_args"]!.AsArray());
    }

    private sealed class CapturingClient : ICopilotClient
    {
        internal byte[]? Sent;
        public ValueTask<HttpResponseMessage> PostResponsesAsync(ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default)
        {
            Sent = body.ToArray();
            return new(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"object":"response","id":"resp_ok","model":"gpt-6-astra","status":"completed","output":[]}""", Encoding.UTF8, "application/json"),
            });
        }
        public ValueTask<HttpResponseMessage> PostMessagesAsync(ReadOnlyMemory<byte> body, bool vision = false, IReadOnlyList<string>? anthropicBeta = null, IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}

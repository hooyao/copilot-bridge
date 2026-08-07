using System.Text.Json.Nodes;
using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract for a clean native Responses → IR → Responses request. The source
/// pushes provider-native data into the IR; the Responses destination pulls it.
/// With no route/profile mutation, every JSON value and input position survives.
/// </summary>
public sealed class CodexRequestFidelityTests
{
    private const string CleanRequest = """
      {
        "model":"gpt-5.6-sol",
        "instructions":"base instructions",
        "input":[
          {
            "type":"additional_tools",
            "role":"developer",
            "tools":[{"type":"custom","name":"exec","future_tool_field":{"v":1}}]
          },
          {
            "type":"message",
            "role":"developer",
            "id":"msg_dev_1",
            "content":[{"type":"input_text","text":"early developer","future_part":"keep"}],
            "future_message":{"a":1}
          },
          {
            "type":"message",
            "role":"user",
            "id":"msg_user_1",
            "content":[{"type":"input_text","text":"run the tools","future_part":[1,2]}],
            "future_message":"user-extra"
          },
          {
            "type":"function_call",
            "id":"fc_1",
            "status":"completed",
            "call_id":"call_1",
            "namespace":"collaboration",
            "name":"list_agents",
            "arguments":"{}",
            "future_call":true
          },
          {
            "type":"function_call_output",
            "id":"fco_1",
            "status":"completed",
            "call_id":"call_1",
            "output":[
              {"type":"input_text","text":"first"},
              {"type":"input_text","text":"second"}
            ],
            "future_output":{"ok":true}
          },
          {
            "type":"reasoning",
            "id":"rs_1",
            "encrypted_content":"opaque-reasoning",
            "summary":[{"type":"summary_text","text":"summary"}],
            "content":[{"type":"reasoning_text","text":"content"}],
            "future_reasoning":7
          },
          {
            "type":"message",
            "role":"assistant",
            "phase":"commentary",
            "status":"completed",
            "content":[{"type":"output_text","text":"working","annotations":[],"future_part":false}],
            "future_message":"assistant-extra"
          },
          {
            "type":"message",
            "role":"developer",
            "id":"msg_dev_2",
            "content":[{"type":"input_text","text":"late developer"}],
            "future_message":{"late":true}
          },
          {
            "type":"message",
            "role":"user",
            "id":"msg_user_2",
            "content":[{"type":"input_text","text":"continue"}]
          }
        ],
        "tool_choice":"auto",
        "parallel_tool_calls":false,
        "reasoning":{
          "effort":"xhigh",
          "summary":"detailed",
          "context":"all_turns",
          "future_reasoning_control":{"mode":"keep"}
        },
        "store":false,
        "stream":true,
        "include":["reasoning.encrypted_content"],
        "prompt_cache_key":"cache-1",
        "text":{"verbosity":"low"},
        "client_metadata":{"turn_id":"turn-1"},
        "future_request":{"nested":[1,{"x":2}]}
      }
      """;

    [Fact]
    public void CleanNativeRequest_PreservesEveryJsonValueAndInputPosition()
    {
        var expected = JsonNode.Parse(CleanRequest);
        var actual = CodexRoundTrip.RoundTrip(CleanRequest);

        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            FirstDifference(expected, actual));
        var wire = actual!.ToJsonString();
        Assert.DoesNotContain("provider_extensions", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("responses_item_extra", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("responses_content_extra", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("responses_system_group", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque_tool_output", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge_", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeFunctionOutputArray_RetainsArrayKindAndValue()
    {
        var emitted = CodexRoundTrip.RoundTrip(CleanRequest).AsObject();
        var output = emitted["input"]!.AsArray()
            .Single(item => item?["type"]?.GetValue<string>() == "function_call_output")!["output"];
        var expected = JsonNode.Parse(CleanRequest)!["input"]!.AsArray()
            .Single(item => item?["type"]?.GetValue<string>() == "function_call_output")!["output"];

        Assert.IsType<JsonArray>(output);
        Assert.True(JsonNode.DeepEquals(expected, output));
    }

    [Fact]
    public void InvalidMessageId_IsOnlyFieldRemovedByProtocolCoercion()
    {
        const string request = """
          {
            "model":"gpt-5.6-sol",
            "input":[{
              "type":"message",
              "role":"assistant",
              "id":"item_0",
              "phase":"commentary",
              "status":"completed",
              "content":[{"type":"output_text","text":"working"}],
              "future_message":{"keep":true}
            }],
            "reasoning":{"effort":"xhigh","context":"all_turns"},
            "stream":true
          }
          """;
        var expected = JsonNode.Parse(request)!.AsObject();
        expected["input"]![0]!.AsObject().Remove("id");

        var actual = CodexRoundTrip.RoundTrip(request);

        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            FirstDifference(expected, actual));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeveloperInvalidMessageId_IsRemovedOnNormalAndRawFallbackPaths(
        bool removeSemanticSystemProjection)
    {
        const string request = """
          {"model":"gpt-5.6-sol","input":[{
            "type":"message","role":"developer","id":"item_0","phase":"commentary",
            "content":[{"type":"input_text","text":"developer"}],"future":"keep"
          }],"stream":true}
          """;
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(request));
        if (removeSemanticSystemProjection)
            ir = ir with { System = null };

        var (body, _, _) = ResponsesRequestBuilder.Build(
            ir,
            new CodexModelProfileCatalog(),
            false,
            out _,
            out _,
            out var mutations);
        var developer = JsonNode.Parse(body)!["input"]![0]!;

        Assert.Null(developer["id"]);
        Assert.Equal("commentary", developer["phase"]!.GetValue<string>());
        Assert.Equal("keep", developer["future"]!.GetValue<string>());
        Assert.Equal(ResponsesRequestMutation.InvalidMessageIdDropped, mutations);
    }

    [Fact]
    public void ExplicitNullReasoningContext_RemainsPresentNull()
    {
        const string request = """
          {"model":"gpt-5.6-sol","input":[],
           "reasoning":{"effort":"xhigh","context":null},"stream":true}
          """;
        var expected = JsonNode.Parse(request);
        var actual = CodexRoundTrip.RoundTrip(request);

        Assert.True(JsonNode.DeepEquals(expected, actual), FirstDifference(expected, actual));
        var reasoning = actual!["reasoning"]!.AsObject();
        Assert.True(reasoning.ContainsKey("context"));
        Assert.Null(reasoning["context"]);
    }

    [Fact]
    public void EmptyMessageProviderExtensions_AreInertOnAnthropicSerialization()
    {
        const string request = """
          {
            "model":"gpt-5.6-sol",
            "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}],
            "stream":true
          }
          """;
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(request));

        Assert.Null(ir.Messages.Single().ProviderExtensions);
        var json = JsonSerializer.Serialize(ir, JsonContext.Default.MessagesRequest);
        Assert.DoesNotContain("provider_extensions", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationCodes_AreEmptyForCleanRequest_AndStableForInvalidId()
    {
        var profiles = new CodexModelProfileCatalog();
        var cleanIr = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(CleanRequest));
        _ = ResponsesRequestBuilder.Build(
            cleanIr, profiles, false, out _, out _, out var cleanMutations);
        Assert.Equal(ResponsesRequestMutation.None, cleanMutations);
        Assert.Equal("", ResponsesRequestBuilder.FormatMutations(cleanMutations));

        const string invalid = """
          {"model":"gpt-5.6-sol","input":[{"type":"message","role":"assistant",
           "id":"item_0","phase":"commentary","content":[{"type":"output_text","text":"x"}]}],
           "stream":true}
          """;
        var invalidIr = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(invalid));
        _ = ResponsesRequestBuilder.Build(
            invalidIr, profiles, false, out _, out _, out var invalidMutations);
        Assert.Equal(ResponsesRequestMutation.InvalidMessageIdDropped, invalidMutations);
        Assert.Equal("protocol.message_id", ResponsesRequestBuilder.FormatMutations(invalidMutations));
    }

    [Fact]
    public async Task T1ProviderPush_IsIndependentOfHeadersAndFutureDestination()
    {
        var adapter = new ResponsesToIrInboundAdapter(
            NullLogger<ResponsesToIrInboundAdapter>.Instance);
        var request = CodexRoundTrip.ParseRequest(CleanRequest);
        var first = await adapter.AdaptAsync(
            request,
            new Dictionary<string, string>(),
            default);
        var second = await adapter.AdaptAsync(
            request,
            new Dictionary<string, string>
            {
                ["x-test-eventual-destination"] = "a-destination-T1-must-not-read",
            },
            default);

        var firstJson = JsonSerializer.Serialize(first, JsonContext.Default.MessagesRequest);
        var secondJson = JsonSerializer.Serialize(second, JsonContext.Default.MessagesRequest);
        Assert.Equal(firstJson, secondJson);
    }

    [Fact]
    public void CorruptProviderConflicts_CannotOverrideSemanticFields_AndAreReported()
    {
        var ir = new MessagesRequest
        {
            Model = "gpt-5.6-sol",
            MaxTokens = 0,
            Stream = true,
            ProviderExtensions = Bag("""
              {"request_extra":{"model":"evil","input":[],"future_request":1}}
              """),
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    ProviderExtensions = Bag("""
                      {"responses_item_extra":{"role":"assistant","content":[],"id":"msg_ok","future_message":2}}
                      """),
                    Content =
                    [
                        new TextBlockParam
                        {
                            Text = "hi",
                            ProviderExtensions = Bag("""
                              {"responses_content_extra":{"type":"bad","text":"evil","future_part":3}}
                              """),
                        },
                    ],
                },
            ],
        };

        var (body, _, _) = ResponsesRequestBuilder.Build(
            ir,
            new CodexModelProfileCatalog(),
            false,
            out _,
            out _,
            out var mutations);
        var json = System.Text.Encoding.UTF8.GetString(body);
        var emitted = JsonNode.Parse(body)!.AsObject();

        Assert.Equal("gpt-5.6-sol", emitted["model"]!.GetValue<string>());
        Assert.Equal("user", emitted["input"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("hi", emitted["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, emitted["future_request"]!.GetValue<int>());
        Assert.Equal(2, emitted["input"]![0]!["future_message"]!.GetValue<int>());
        Assert.Equal(3, emitted["input"]![0]!["content"]![0]!["future_part"]!.GetValue<int>());
        Assert.Equal(1, CountToken(json, "\"model\""));
        Assert.Equal(1, CountToken(json, "\"role\""));
        Assert.Equal(1, CountToken(json, "\"content\""));
        Assert.Equal(1, CountToken(json, "\"text\""));
        Assert.Equal(ResponsesRequestMutation.ProviderConflictDropped, mutations);
        Assert.Equal("protocol.provider_conflict", ResponsesRequestBuilder.FormatMutations(mutations));
    }

    [Fact]
    public void AnthropicDestination_IgnoresOpenAiCarrier_AndCleanPathAllocatesNoProjection()
    {
        const string simple = """
          {"model":"gpt-5.6-sol","input":[{"type":"message","role":"user",
           "content":[{"type":"input_text","text":"hi"}]}],"stream":true}
          """;
        var clean = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(simple));
        Assert.Same(clean, AnthropicRequestWire.Project(clean));

        var carried = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(CleanRequest));
        var projected = AnthropicRequestWire.Project(carried);
        var wire = JsonSerializer.Serialize(projected, JsonContext.Default.MessagesRequest);
        Assert.DoesNotContain("provider_extensions", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("responses_item_extra", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge_", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticSystemEdit_PatchesDeveloperItemWithoutRestoringStaleText()
    {
        const string request = """
          {"model":"gpt-5.6-sol","instructions":"base","input":[
            {"type":"message","role":"developer","id":"msg_dev","future":"keep",
             "content":[{"type":"input_text","text":"original","future_part":1}]},
            {"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}
          ],"stream":true}
          """;
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(request));
        var edited = ir with
        {
            System = ir.System!.Select(part =>
                part.Text == "original" ? part with { Text = "sanitized" } : part).ToArray(),
        };

        var emitted = JsonNode.Parse(CodexRoundTrip.ToResponsesWire(edited))!.AsObject();
        var developer = emitted["input"]![0]!;
        Assert.Equal("sanitized", developer["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, developer["content"]![0]!["future_part"]!.GetValue<int>());
        Assert.Equal("keep", developer["future"]!.GetValue<string>());
        Assert.Equal("base", emitted["instructions"]!.GetValue<string>());
    }

    private static ProviderExtensions Bag(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement>
            {
                [ProviderExtensions.OpenAiNamespace] = doc.RootElement.Clone(),
            },
        };
    }

    private static int CountToken(string text, string token)
    {
        var count = 0;
        for (var start = 0; (start = text.IndexOf(token, start, StringComparison.Ordinal)) >= 0;
             start += token.Length)
            count++;
        return count;
    }

    private static string FirstDifference(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is null || actual is null)
            return expected is null && actual is null ? "<none>" : path;
        if (expected.GetValueKind() != actual.GetValueKind())
            return $"{path}: {expected.GetValueKind()} != {actual.GetValueKind()}";
        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (var property in expectedObject)
                if (!actualObject.ContainsKey(property.Key)) return $"{path}.{property.Key}: missing";
            foreach (var property in actualObject)
                if (!expectedObject.ContainsKey(property.Key)) return $"{path}.{property.Key}: extra";
            foreach (var property in expectedObject)
            {
                var difference = FirstDifference(
                    property.Value,
                    actualObject[property.Key],
                    $"{path}.{property.Key}");
                if (difference != "<none>") return difference;
            }
            return "<none>";
        }
        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
                return $"{path}: count {expectedArray.Count} != {actualArray.Count}";
            for (var i = 0; i < expectedArray.Count; i++)
            {
                var difference = FirstDifference(expectedArray[i], actualArray[i], $"{path}[{i}]");
                if (difference != "<none>") return difference;
            }
            return "<none>";
        }
        return JsonNode.DeepEquals(expected, actual) ? "<none>" : $"{path}: value differs";
    }
}

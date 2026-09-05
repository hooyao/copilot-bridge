using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract for Codex 0.153.3 standalone named function outputs
/// (openai/codex #39782): an external tool event may have no preceding model
/// call, so <c>name</c> identifies it while <c>call_id</c> is absent.
/// </summary>
public class CodexStandaloneFunctionOutputTests
{
    [Fact]
    public void StandaloneNamedOutput_RoundTripsAuthorityShape_Value_AndOrder()
    {
        const string body = """
          {
            "model":"gpt-5.6-sol-fast",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"before"}]},
              {
                "type":"function_call_output",
                "id":"fco_standalone_1",
                "name":"automation_update",
                "namespace":"codex_app",
                "output":"<heartbeat>run now</heartbeat>",
                "future_metadata":{"source":"scheduler","attempt":2}
              },
              {"type":"message","role":"user","content":[{"type":"input_text","text":"after"}]}
            ],
            "stream":true,
            "store":false
          }
          """;

        var input = CodexRoundTrip.RoundTrip(body).AsObject()["input"]!.AsArray();
        Assert.Equal(3, input.Count);

        var standalone = input[1]!.AsObject();
        Assert.Equal("function_call_output", standalone["type"]!.GetValue<string>());
        Assert.False(standalone.ContainsKey("call_id"));
        Assert.Equal("fco_standalone_1", standalone["id"]!.GetValue<string>());
        Assert.Equal("automation_update", standalone["name"]!.GetValue<string>());
        Assert.Equal("codex_app", standalone["namespace"]!.GetValue<string>());
        Assert.Equal("<heartbeat>run now</heartbeat>", standalone["output"]!.GetValue<string>());
        Assert.Equal("scheduler",
            standalone["future_metadata"]!["source"]!.GetValue<string>());
        Assert.Equal(2, standalone["future_metadata"]!["attempt"]!.GetValue<int>());

        Assert.Equal("before", ReadMessageText(input[0]!.AsObject()));
        Assert.Equal("after", ReadMessageText(input[2]!.AsObject()));
    }

    [Fact]
    public void PairedOutput_RetainsCallId_AndExistingSemanticPath()
    {
        const string body = """
          {
            "model":"gpt-5.6-sol-fast",
            "input":[
              {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"},
              {"type":"function_call_output","id":"fco_1","call_id":"call_1","name":"lookup","namespace":"codex_app","output":"done"}
            ],
            "stream":true,
            "store":false
          }
          """;

        var input = CodexRoundTrip.RoundTrip(body).AsObject()["input"]!.AsArray();
        var output = input.Single(item =>
            item!["type"]!.GetValue<string>() == "function_call_output")!.AsObject();

        Assert.Equal("call_1", output["call_id"]!.GetValue<string>());
        Assert.Equal("fco_1", output["id"]!.GetValue<string>());
        Assert.Equal("done", output["output"]!.GetValue<string>());
        Assert.Equal("lookup", output["name"]!.GetValue<string>());
        Assert.Equal("codex_app", output["namespace"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"plain text\"")]
    [InlineData("{\"rows\":[1,2],\"ok\":true}")]
    [InlineData("[{\"type\":\"input_text\",\"text\":\"first\"},{\"type\":\"input_text\",\"text\":\"second\"}]")]
    [InlineData("null")]
    public void StandaloneOutput_PreservesEveryJsonKindAndValue(string outputJson)
    {
        var body = $$"""
          {
            "model":"gpt-5.6-sol-fast",
            "input":[{
              "type":"function_call_output",
              "name":"notifications",
              "namespace":"codex_app",
              "output":{{outputJson}}
            }],
            "stream":true,
            "store":false
          }
          """;

        var request = CodexRoundTrip.ParseRequest(body);
        var wire = Encoding.UTF8.GetString(
            CodexRoundTrip.ToResponsesWire(CodexRoundTrip.ToIr(request)));
        using var expected = JsonDocument.Parse(outputJson);
        using var actual = JsonDocument.Parse(wire);
        var actualOutput = actual.RootElement.GetProperty("input")[0].GetProperty("output");

        Assert.Equal(expected.RootElement.ValueKind, actualOutput.ValueKind);
        Assert.True(
            JsonElement.DeepEquals(expected.RootElement, actualOutput),
            $"standalone output changed from {outputJson} to {actualOutput.GetRawText()}");
    }

    [Fact]
    public void UnpairedNamelessOutput_IsRejectedWithUnionInvariant()
    {
        const string body = """
          {
            "model":"gpt-5.6-sol-fast",
            "input":[{"type":"function_call_output","output":"orphan"}],
            "stream":true,
            "store":false
          }
          """;

        var error = Assert.Throws<JsonException>(() => CodexRoundTrip.ParseRequest(body));
        Assert.Contains("non-empty 'call_id' or 'name'", error.Message, StringComparison.Ordinal);
    }

    private static string ReadMessageText(JsonObject item) =>
        item["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
}

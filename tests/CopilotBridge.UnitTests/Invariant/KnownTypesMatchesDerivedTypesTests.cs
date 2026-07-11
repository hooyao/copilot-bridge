using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Responses;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Guards the converter's known-vs-unknown routing against drift. The converter derives
/// its known-type set from the source-generated <c>[JsonDerivedType]</c> metadata (not a
/// hand-maintained list), so drift is unrepresentable BY CONSTRUCTION in both directions
/// — adding/removing a <c>[JsonDerivedType]</c> automatically moves the type between the
/// typed-bind and opaque-passthrough branches. This test locks in the OBSERVABLE
/// consequence: every attributed type binds to its record (not
/// <see cref="ResponsesUnknownItem"/>), and a type WITHOUT an attribute routes to
/// unknown. If the derivation ever regressed to a stale hand-list, one of these reddens.
/// </summary>
public class KnownTypesMatchesDerivedTypesTests
{
    [Fact]
    public void EveryDerivedTypeDiscriminator_BindsToItsTypedRecord_NotUnknown()
    {
        var discriminators = typeof(ResponsesInputItem)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => a.TypeDiscriminator as string)
            .Where(s => s is not null)
            .ToList();

        Assert.NotEmpty(discriminators); // sanity: attributes are present

        foreach (var type in discriminators)
        {
            // A minimal item of this type, wrapped in a request. Each modeled type needs
            // its required fields present so binding succeeds; the point is only that the
            // converter routes it to a typed record, not to ResponsesUnknownItem.
            var itemJson = MinimalItemJson(type!);
            var body = $$"""
              {"model":"gpt-5.6-sol","input":[{{itemJson}}],"stream":true,"store":false}
              """;
            var req = JsonSerializer.Deserialize(body, JsonContext.Default.ResponsesRequest);
            Assert.NotNull(req);
            var item = Assert.Single(req!.Input);
            Assert.False(item is ResponsesUnknownItem,
                $"type '{type}' has a [JsonDerivedType] but the converter routed it to " +
                $"ResponsesUnknownItem — the derived known-type set is wrong.");
        }
    }

    [Theory]
    // Opaque items the bridge deliberately does NOT model (no [JsonDerivedType]) — they
    // must route to the unknown passthrough so every sibling field survives verbatim.
    [InlineData("tool_search_call")]
    [InlineData("agent_message")]
    [InlineData("additional_tools")]
    [InlineData("compaction")]
    public void AnUnmodeledType_RoutesToUnknown_ConfirmingTheTestDiscriminates(string type)
    {
        var body = $$"""
          {"model":"gpt-5.6-sol","input":[{"type":"{{type}}","x":1}],"stream":true,"store":false}
          """;
        var req = JsonSerializer.Deserialize(body, JsonContext.Default.ResponsesRequest);
        var item = Assert.Single(req!.Input);
        Assert.IsType<ResponsesUnknownItem>(item);
    }

    private static string MinimalItemJson(string type) => type switch
    {
        "message" => """{"type":"message","role":"user","content":[{"type":"input_text","text":"x"}]}""",
        "function_call" => """{"type":"function_call","call_id":"c","name":"f","arguments":"{}"}""",
        "function_call_output" => """{"type":"function_call_output","call_id":"c","output":"ok"}""",
        "reasoning" => """{"type":"reasoning","encrypted_content":"blob"}""",
        _ => throw new Xunit.Sdk.XunitException(
            $"MinimalItemJson has no sample for modeled type '{type}' — add one so the drift " +
            $"guard covers it (a new [JsonDerivedType] was added without updating this test)."),
    };
}

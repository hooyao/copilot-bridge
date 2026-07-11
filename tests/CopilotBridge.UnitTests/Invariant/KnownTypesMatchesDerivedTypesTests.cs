using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Responses;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Guards the drift hazard the converter comment warns about: the converter's
/// <c>KnownTypes</c> set must stay in sync with the <c>[JsonDerivedType]</c>
/// discriminators on <see cref="ResponsesInputItem"/>. If they drift, a modeled type
/// silently routes through the unknown-item passthrough instead of binding to its
/// typed record — losing whatever T1 interpretation that type needs, with no error.
/// </summary>
/// <remarks>
/// Rather than reflect into the converter's private set, this asserts the OBSERVABLE
/// contract: every type carrying a <c>[JsonDerivedType]</c> attribute must deserialize
/// to its typed record (NOT <see cref="ResponsesUnknownItem"/>) when fed through the
/// real converter. A discriminator missing from <c>KnownTypes</c> would make its item
/// come back as <c>ResponsesUnknownItem</c> and redden this test.
/// </remarks>
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
                $"ResponsesUnknownItem — KnownTypes is out of sync (drift).");
        }
    }

    [Fact]
    public void AnUnmodeledType_DoesRouteToUnknown_ConfirmingTheTestDiscriminates()
    {
        // Control: a type with NO [JsonDerivedType] must land in ResponsesUnknownItem.
        // (If this failed, the test above would be vacuous.)
        var body = """
          {"model":"gpt-5.6-sol","input":[{"type":"tool_search_call","call_id":"c","execution":"client"}],"stream":true,"store":false}
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
        "additional_tools" => """{"type":"additional_tools","role":"developer","tools":[]}""",
        _ => throw new Xunit.Sdk.XunitException(
            $"MinimalItemJson has no sample for modeled type '{type}' — add one so the drift " +
            $"guard covers it (a new [JsonDerivedType] was added without updating this test)."),
    };
}

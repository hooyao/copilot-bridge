using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Direct contract tests for <see cref="JsonSchemaSubsetValidator"/> — the recursive
/// subset validator behind the tool-input detector. Asserts the documented contract:
/// required-field presence, primitive type matching (including the integer/number
/// split), recursive descent into nested <c>properties</c> and array <c>items</c>,
/// and — critically — that every unmodeled keyword fails OPEN (a pass means "not
/// obviously invalid", not "fully schema-valid"). Testing the algorithm directly is
/// the reason it was extracted from the detector into its own type.
/// </summary>
public class JsonSchemaSubsetValidatorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static InputSchema Schema(string type = "object", string? propertiesJson = null, string[]? required = null) =>
        new()
        {
            Type = type,
            Properties = propertiesJson is null ? null : Json(propertiesJson),
            Required = required,
        };

    [Fact]
    public void TopLevelRequiredPresent_Passes()
    {
        var schema = Schema(propertiesJson: """{"a":{"type":"string"}}""", required: ["a"]);
        Assert.True(JsonSchemaSubsetValidator.Validate(schema, Json("""{"a":"x"}"""), out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void TopLevelRequiredMissing_FailsWithPath()
    {
        var schema = Schema(propertiesJson: """{"a":{"type":"string"}}""", required: ["a"]);
        Assert.False(JsonSchemaSubsetValidator.Validate(schema, Json("""{"b":1}"""), out var reason));
        Assert.Contains("$.a is required", reason);
    }

    [Fact]
    public void NestedRequiredMissing_FailsRecursively()
    {
        // required lives inside items, so it is only reachable by recursing the raw
        // properties JsonElement (not InputSchema.Required).
        var schema = Schema(propertiesJson: """
            {"list":{"type":"array","items":{"type":"object","properties":{"k":{"type":"string"}},"required":["k"]}}}
            """);
        Assert.False(JsonSchemaSubsetValidator.Validate(schema, Json("""{"list":[{"other":1}]}"""), out var reason));
        Assert.Contains("is required", reason);
    }

    [Theory]
    [InlineData("string", "\"hi\"", true)]
    [InlineData("string", "5", false)]
    [InlineData("boolean", "true", true)]
    [InlineData("boolean", "\"true\"", false)]
    [InlineData("number", "1.5", true)]
    [InlineData("number", "\"1.5\"", false)]
    [InlineData("array", "[]", true)]
    [InlineData("array", "\"[]\"", false)]
    [InlineData("object", "{}", true)]
    [InlineData("object", "[]", false)]
    public void PrimitiveTypeMatching_EnforcesDeclaredType(string type, string valueJson, bool expectValid)
    {
        var schema = Schema(propertiesJson: "{\"v\":{\"type\":\"" + type + "\"}}");
        var result = JsonSchemaSubsetValidator.Validate(schema, Json("{\"v\":" + valueJson + "}"), out _);
        Assert.Equal(expectValid, result);
    }

    [Theory]
    [InlineData("5", true)]     // plain integer
    [InlineData("100.0", true)] // integral value in decimal form — schema-valid integer
    [InlineData("1e2", true)]   // integral value in exponent form — schema-valid integer
    [InlineData("1.5", false)]  // genuine fractional — not an integer
    public void IntegerType_AcceptsIntegralValues_RegardlessOfNotation(string valueJson, bool expectValid)
    {
        // Contract: a JSON-Schema integer permits any number with no fractional part.
        // The prior TryGetInt64-only check wrongly rejected 100.0 / 1e2 — that is a
        // false positive in the abort direction, which this guard must avoid.
        var schema = Schema(propertiesJson: """{"n":{"type":"integer"}}""");
        var result = JsonSchemaSubsetValidator.Validate(schema, Json("{\"n\":" + valueJson + "}"), out _);
        Assert.Equal(expectValid, result);
    }

    [Fact]
    public void UnmodeledKeywords_FailOpen()
    {
        // enum / minimum / additionalProperties are not modeled. A value that violates
        // them must still PASS — the validator only rejects what it can positively
        // prove wrong (type/required), never guesses.
        var schema = Schema(propertiesJson: """
            {"color":{"type":"string","enum":["red","green"]},"n":{"type":"integer","minimum":10}}
            """);
        Assert.True(JsonSchemaSubsetValidator.Validate(schema, Json("""{"color":"purple","n":1}"""), out _));
    }

    [Fact]
    public void EmptySchema_AcceptsAnyObject()
    {
        // No properties, no required — nothing to prove wrong, so any object passes.
        Assert.True(JsonSchemaSubsetValidator.Validate(Schema(), Json("""{"anything":true}"""), out _));
    }

    [Fact]
    public void PropertyPresentButWrongNestedType_Fails()
    {
        var schema = Schema(propertiesJson: """{"outer":{"type":"object","properties":{"inner":{"type":"string"}}}}""");
        Assert.False(JsonSchemaSubsetValidator.Validate(schema, Json("""{"outer":{"inner":5}}"""), out var reason));
        Assert.Contains("inner", reason);
    }
}

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

    /// <summary>
    /// Tests: a declared top-level required property that is present passes cleanly.
    /// Input: schema requiring <c>a</c>; value <c>{"a":"x"}</c>.
    /// Expects: <c>Validate</c> returns true and <c>reason</c> is empty.
    /// </summary>
    [Fact]
    public void TopLevelRequiredPresent_Passes()
    {
        var schema = Schema(propertiesJson: """{"a":{"type":"string"}}""", required: ["a"]);
        Assert.True(JsonSchemaSubsetValidator.Validate(schema, Json("""{"a":"x"}"""), out var reason));
        Assert.Equal("", reason);
    }

    /// <summary>
    /// Tests: a missing top-level required property fails, and the failure reason names
    /// the JSON path so an operator can see which field is missing.
    /// Input: schema requiring <c>a</c>; value <c>{"b":1}</c> (has <c>b</c>, not <c>a</c>).
    /// Expects: <c>Validate</c> returns false and <c>reason</c> contains
    /// <c>$.a is required</c>.
    /// </summary>
    [Fact]
    public void TopLevelRequiredMissing_FailsWithPath()
    {
        var schema = Schema(propertiesJson: """{"a":{"type":"string"}}""", required: ["a"]);
        Assert.False(JsonSchemaSubsetValidator.Validate(schema, Json("""{"b":1}"""), out var reason));
        Assert.Contains("$.a is required", reason);
    }

    /// <summary>
    /// Tests: a required property declared deep inside <c>items</c> is enforced through
    /// recursive descent — nested <c>required</c> lives in the raw properties JSON, not in
    /// <see cref="InputSchema.Required"/>, so this proves the recursion reads it.
    /// Input: schema for <c>list: array of objects</c> whose items require <c>k</c>;
    /// value <c>{"list":[{"other":1}]}</c> (item lacks <c>k</c>).
    /// Expects: <c>Validate</c> returns false and <c>reason</c> contains
    /// <c>is required</c>.
    /// </summary>
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

    /// <summary>
    /// Tests: each primitive schema <c>type</c> accepts a matching JSON value and rejects
    /// a mismatching one (the type-matching table). The stringified cases (<c>"5"</c> for
    /// number, <c>"[]"</c> for array) guard against accepting a coerced value.
    /// Input: (declared type, value JSON, expected valid) rows — e.g. string/"hi"/true,
    /// string/5/false, array/[]/true, array/"[]"/false, object/{}/true, object/[]/false.
    /// Expects: <c>Validate</c> returns the row's expected boolean.
    /// </summary>
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

    /// <summary>
    /// Tests: the <c>integer</c> type accepts any mathematically-integral number
    /// regardless of notation, and rejects a genuine fraction. This guards the fix for
    /// the prior <c>TryGetInt64</c>-only check, which falsely rejected <c>100.0</c> /
    /// <c>1e2</c> (a false positive in the abort direction).
    /// Input: (value JSON, expected valid) — 5/true, 100.0/true, 1e2/true, 1.5/false.
    /// Expects: <c>Validate</c> returns the row's expected boolean.
    /// </summary>
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

    /// <summary>
    /// Tests: the validator fails OPEN on schema keywords it does not model — the core
    /// safety property. It only rejects what it can positively prove wrong (type /
    /// required), never guesses on unmodeled constraints.
    /// Input: a schema using <c>enum</c> and <c>minimum</c>; a value that violates both
    /// (<c>{"color":"purple","n":1}</c>) but whose declared types still match.
    /// Expects: <c>Validate</c> returns true (the unmodeled violations are ignored).
    /// </summary>
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

    /// <summary>
    /// Tests: a schema with no <c>properties</c> and no <c>required</c> constrains
    /// nothing, so any object is accepted (the minimal-schema case behind the detector's
    /// "unknown tool → only require an object" behaviour).
    /// Input: an empty object schema; value <c>{"anything":true}</c>.
    /// Expects: <c>Validate</c> returns true.
    /// </summary>
    [Fact]
    public void EmptySchema_AcceptsAnyObject()
    {
        // No properties, no required — nothing to prove wrong, so any object passes.
        Assert.True(JsonSchemaSubsetValidator.Validate(Schema(), Json("""{"anything":true}"""), out _));
    }

    /// <summary>
    /// Tests: a property present but with the wrong type at a nested level fails, and the
    /// reason names the offending nested property (recursion into <c>properties</c>).
    /// Input: schema <c>outer: object with inner: string</c>; value
    /// <c>{"outer":{"inner":5}}</c> (<c>inner</c> is a number).
    /// Expects: <c>Validate</c> returns false and <c>reason</c> contains <c>inner</c>.
    /// </summary>
    [Fact]
    public void PropertyPresentButWrongNestedType_Fails()
    {
        var schema = Schema(propertiesJson: """{"outer":{"type":"object","properties":{"inner":{"type":"string"}}}}""");
        Assert.False(JsonSchemaSubsetValidator.Validate(schema, Json("""{"outer":{"inner":5}}"""), out var reason));
        Assert.Contains("inner", reason);
    }
}

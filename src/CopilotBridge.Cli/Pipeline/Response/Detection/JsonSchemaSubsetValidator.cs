using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// A deliberately partial ("subset") validator of a JSON value against an Anthropic
/// tool <see cref="InputSchema"/>. Used by <see cref="ToolInputValidationDetector"/>
/// to reject a real <c>tool_use.input</c> that obviously violates the declared tool
/// schema before it reaches Claude Code.
/// </summary>
/// <remarks>
/// <para>
/// Extracted to its own <c>internal</c> type (mirroring the standalone
/// <see cref="ResponseLeakAutomaton"/>/<see cref="KmpMatcher"/> siblings in this
/// folder) so this recursive algorithm can be unit-tested directly rather than only
/// through the full SSE-stream detector.
/// </para>
/// <para>
/// <b>What it checks</b> — only what the lossy <see cref="InputSchema"/> model carries
/// plus what survives inside the raw <c>properties</c> element: the top-level value
/// must be an object; declared <c>required</c> properties must be present; declared
/// <c>type</c>s (<c>object</c>/<c>array</c>/<c>string</c>/<c>boolean</c>/<c>number</c>/
/// <c>integer</c>/<c>null</c>) must match; and nested <c>properties</c> and array
/// <c>items</c> are checked recursively (including nested <c>required</c>).
/// </para>
/// <para>
/// <b>Fail-open by design</b> — every keyword it does not model (<c>enum</c>,
/// <c>const</c>, <c>pattern</c>, <c>minimum</c>/<c>maximum</c>, <c>oneOf</c>,
/// <c>$ref</c>, <c>additionalProperties</c>, union <c>type</c> arrays, tuple
/// <c>items</c>, …) is silently accepted. A <c>true</c> result therefore means "not
/// obviously invalid", NOT "fully schema-valid". This is the safe direction for a
/// guard whose false positive is aborting a legitimate response.
/// </para>
/// </remarks>
internal static class JsonSchemaSubsetValidator
{
    public static bool Validate(InputSchema schema, JsonElement value, out string reason)
    {
        using var schemaDoc = JsonDocument.Parse(SchemaToJson(schema));
        return ValidateAgainstSchema(schemaDoc.RootElement, value, "$", out reason);
    }

    private static string SchemaToJson(InputSchema schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", schema.Type);
            if (schema.Properties is { } properties)
            {
                writer.WritePropertyName("properties");
                properties.WriteTo(writer);
            }
            if (schema.Required is { Count: > 0 } required)
            {
                writer.WritePropertyName("required");
                writer.WriteStartArray();
                foreach (var name in required)
                {
                    writer.WriteStringValue(name);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool ValidateAgainstSchema(JsonElement schema, JsonElement value, string path, out string reason)
    {
        var expectedType = TryGetType(schema);
        if (expectedType is not null && !MatchesType(value, expectedType))
        {
            reason = $"{path} expected {expectedType} but got {KindName(value.ValueKind)}";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var req in required.EnumerateArray())
                {
                    if (req.ValueKind != JsonValueKind.String) continue;
                    var name = req.GetString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!value.TryGetProperty(name, out _))
                    {
                        reason = $"{path}.{name} is required";
                        return false;
                    }
                }
            }

            if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in properties.EnumerateObject())
                {
                    if (value.TryGetProperty(prop.Name, out var child)
                        && !ValidateAgainstSchema(prop.Value, child, path + "." + prop.Name, out reason))
                    {
                        return false;
                    }
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array
                 && schema.TryGetProperty("items", out var items)
                 && items.ValueKind == JsonValueKind.Object)
        {
            var idx = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (!ValidateAgainstSchema(items, item, path + "[" + idx + "]", out reason))
                {
                    return false;
                }
                idx++;
            }
        }

        reason = "";
        return true;
    }

    private static string? TryGetType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type)) return null;
        return type.ValueKind == JsonValueKind.String ? type.GetString() : null;
    }

    private static bool MatchesType(JsonElement value, string expected) => expected switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        // A JSON-Schema integer permits any number with no fractional part, including
        // the exponent form (1e2) and values outside Int64 range. TryGetInt64 rejects
        // all of those — too strict, and wrong in the abort direction — so accept any
        // Number whose value is mathematically integral.
        "integer" => value.ValueKind == JsonValueKind.Number && IsIntegral(value),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    /// <summary>
    /// True when a JSON number has no fractional part (so it satisfies JSON-Schema
    /// <c>integer</c>), independent of Int64 range or exponent notation. Falls back to
    /// the raw text when <see cref="double"/> parsing fails, so an out-of-double-range
    /// literal is accepted rather than falsely rejected.
    /// </summary>
    private static bool IsIntegral(JsonElement value)
    {
        if (value.TryGetInt64(out _))
        {
            return true;
        }
        if (value.TryGetDouble(out var d))
        {
            return !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d;
        }
        // Unparseable as double (e.g. a literal beyond double's range): treat a raw
        // token with no '.', 'e', or 'E' as integral. Fail open otherwise.
        var raw = value.GetRawText();
        return raw.IndexOfAny(['.', 'e', 'E']) < 0;
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => kind.ToString(),
    };
}

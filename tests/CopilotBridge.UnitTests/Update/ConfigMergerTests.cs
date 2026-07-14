using System.Text.Json;
using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="ConfigMerger"/> from the "Template-based
/// configuration migration" requirement. Assertions read merged values back via
/// JsonDocument so they check observable structure, not implementation detail.
/// </summary>
public class ConfigMergerTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Existing_scalar_preserves_operator_value()
    {
        var merged = ConfigMerger.Merge(
            oldJson: """{ "Server": { "Port": 19000 } }""",
            newJson: """{ "Server": { "Port": 8765 } }""");

        Assert.Equal(19000, Root(merged).GetProperty("Server").GetProperty("Port").GetInt32());
    }

    [Fact]
    public void New_only_setting_receives_new_default()
    {
        var merged = ConfigMerger.Merge(
            oldJson: """{ "Server": { "Port": 19000 } }""",
            newJson: """{ "Server": { "Port": 8765, "RequestTimeoutSeconds": 120 } }""");

        var server = Root(merged).GetProperty("Server");
        Assert.Equal(19000, server.GetProperty("Port").GetInt32());
        Assert.Equal(120, server.GetProperty("RequestTimeoutSeconds").GetInt32());
    }

    [Fact]
    public void Removed_old_only_key_is_dropped()
    {
        var merged = ConfigMerger.Merge(
            oldJson: """{ "RemovedLegacyOption": true, "Server": { "Port": 19000 } }""",
            newJson: """{ "Server": { "Port": 8765 } }""");

        Assert.False(Root(merged).TryGetProperty("RemovedLegacyOption", out _));
    }

    [Fact]
    public void Objects_merge_recursively()
    {
        var merged = ConfigMerger.Merge(
            oldJson: """{ "Tracing": { "Enabled": true } }""",
            newJson: """{ "Tracing": { "Enabled": false, "Directory": "request-traces" } }""");

        var tracing = Root(merged).GetProperty("Tracing");
        Assert.True(tracing.GetProperty("Enabled").GetBoolean());
        Assert.Equal("request-traces", tracing.GetProperty("Directory").GetString());
    }

    [Fact]
    public void Locations_array_is_replaced_atomically()
    {
        var oldJson = """
        { "Routing": { "Locations": [ { "When": { "Model": "a" }, "Use": { "Model": "b" } } ] } }
        """;
        var newJson = """
        { "Routing": { "Locations": [ { "When": { "Model": "x" }, "Use": { "Model": "y" } },
                                      { "When": { "Model": "p" }, "Use": { "Model": "q" } } ] } }
        """;

        var merged = ConfigMerger.Merge(oldJson, newJson);
        var locations = Root(merged).GetProperty("Routing").GetProperty("Locations");

        // Whole old array wins — exactly one element, and it's the old one.
        Assert.Equal(1, locations.GetArrayLength());
        Assert.Equal("a", locations[0].GetProperty("When").GetProperty("Model").GetString());
    }

    [Fact]
    public void Empty_old_array_remains_empty()
    {
        var merged = ConfigMerger.Merge(
            oldJson: """{ "Routing": { "Locations": [] } }""",
            newJson: """{ "Routing": { "Locations": [ { "When": {}, "Use": {} } ] } }""");

        Assert.Equal(0, Root(merged).GetProperty("Routing").GetProperty("Locations").GetArrayLength());
    }

    [Fact]
    public void Old_null_overrides_new_default()
    {
        var merged = ConfigMerger.Merge(
            oldJson: """{ "Feature": null }""",
            newJson: """{ "Feature": "on" }""");

        Assert.Equal(JsonValueKind.Null, Root(merged).GetProperty("Feature").ValueKind);
    }

    [Fact]
    public void Type_mismatched_old_subtree_remains_atomic_with_nested_old_only_content()
    {
        // New "Feature" is a scalar; old is an object with an old-only "Legacy".
        // Old-only deletion applies ONLY at object/object frontiers — a type
        // mismatch copies the complete old subtree, so "Legacy" survives.
        var merged = ConfigMerger.Merge(
            oldJson: """{ "Feature": { "Enabled": true, "Legacy": 5 } }""",
            newJson: """{ "Feature": "scalar-default" }""");

        var feature = Root(merged).GetProperty("Feature");
        Assert.Equal(JsonValueKind.Object, feature.ValueKind);
        Assert.True(feature.GetProperty("Enabled").GetBoolean());
        Assert.Equal(5, feature.GetProperty("Legacy").GetInt32());
    }

    [Fact]
    public void Output_property_spelling_comes_from_new_template()
    {
        // Old uses lowercase 'server'; template uses 'Server'. Value is taken
        // from old (case-insensitive match) but the KEY spelling is the template's.
        var merged = ConfigMerger.Merge(
            oldJson: """{ "server": { "port": 19000 } }""",
            newJson: """{ "Server": { "Port": 8765 } }""");

        var root = Root(merged);
        Assert.True(root.TryGetProperty("Server", out var server));
        Assert.False(root.TryGetProperty("server", out _));
        Assert.True(server.TryGetProperty("Port", out var port));
        Assert.Equal(19000, port.GetInt32());
    }

    [Fact]
    public void Case_insensitive_duplicate_keys_fail_the_merge()
    {
        var ex = Assert.Throws<ConfigMergeException>(() => ConfigMerger.Merge(
            oldJson: """{ "Server": {}, "server": {} }""",
            newJson: """{ "Server": {} }"""));

        Assert.Contains("duplicate", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_comments_and_trailing_commas_like_the_config_provider()
    {
        var merged = ConfigMerger.Merge(
            oldJson: """
            {
                // operator note
                "Server": { "Port": 19000, },
            }
            """,
            newJson: """{ "Server": { "Port": 8765 } }""");

        Assert.Equal(19000, Root(merged).GetProperty("Server").GetProperty("Port").GetInt32());
    }
}

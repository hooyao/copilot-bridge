using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// CI-safe B3 guard for the committed Responses backend facts. The live
/// Playground sweep writes this snapshot; this test ensures every rewrite-driving
/// fact remains connected to the shipping catalog on every pull request.
/// </summary>
public sealed class ResponsesCatalogSnapshotContractTests
{
    [Fact]
    public void CommittedResponsesSnapshotMatchesShippingCatalog()
    {
        var snapshot = JsonNode.Parse(File.ReadAllText(SnapshotPath()))?.AsObject()
            ?? throw new InvalidDataException("Responses contract snapshot is not a JSON object.");
        var models = Assert.IsType<JsonObject>(snapshot["models"]);
        var catalog = new CodexModelProfileCatalog();

        Assert.Equal(
            catalog.KnownIds.Order(StringComparer.Ordinal),
            models.Select(entry => entry.Key).Order(StringComparer.Ordinal));

        foreach (var model in catalog.KnownIds)
        {
            var profile = Assert.IsType<CodexModelProfile>(catalog.Get(model));
            var facts = Assert.IsType<JsonObject>(models[model]);
            var effort = Assert.IsType<JsonObject>(facts["effort"]);
            var accepted = Assert.IsType<JsonArray>(effort["accepted"])
                .Select(value => value!.GetValue<string>())
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(profile.AcceptedEfforts.Order(StringComparer.Ordinal), accepted);

            var fieldsRejected = Strings(facts, "fields_rejected");
            Assert.Equal(
                CodexModelProfileCatalog.StripsServiceTier,
                fieldsRejected.Contains("service_tier"));
            Assert.Equal(
                CodexModelProfileCatalog.StripsStoreTrue,
                fieldsRejected.Contains("store_true"));

            var toolsRejected = Strings(facts, "tools_rejected");
            Assert.Equal(
                CodexModelProfileCatalog.DropsImageGenerationTool,
                toolsRejected.Contains("image_generation"));
            Assert.Equal(profile.RejectsCustomTools, toolsRejected.Contains("custom_apply_patch"));
            Assert.Equal(
                profile.SupportsMultimodalFunctionOutput,
                facts["supports_multimodal_function_output"]?.GetValue<bool>());
        }
    }

    private static HashSet<string> Strings(JsonObject facts, string name) =>
        Assert.IsType<JsonArray>(facts[name])
            .Select(value => value!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

    private static string SnapshotPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "docs", "copilot-responses-contract-snapshot.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "Could not locate docs/copilot-responses-contract-snapshot.json.");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.Playground;

[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexCatalogSnapshotContractTests
{
    [Fact]
    public void Captured_live_model_bytes_project_truthful_context_and_fail_closed_mutations()
    {
        var snapshot = LoadSnapshot();
        Assert.Equal(9, snapshot.Count);
        var projected = Project(snapshot);
        var reviewedBaseline = Project([]);

        foreach (var slug in new[] { "gpt-5.4", "gpt-5.5", "gpt-5.6-luna", "gpt-5.6-sol", "gpt-5.6-terra" })
        {
            var model = Find(projected.Models, slug);
            Assert.Equal(1_050_000, model.GetProperty("context_window").GetInt32());
            Assert.Equal(1_050_000, model.GetProperty("max_context_window").GetInt32());
            Assert.Equal(898_000, model.GetProperty("auto_compact_token_limit").GetInt32());
        }

        Assert.Equal(400_000, Find(projected.Models, "gpt-5.4-mini").GetProperty("context_window").GetInt32());

        // Mutate only one captured capability axis at a time. Missing/inconsistent
        // live limits keep reviewed values, a retired model is hidden, and a
        // future live-only slug is never synthesized.
        var missing = Mutate(snapshot, "gpt-5.4", model =>
            model["capabilities"]!["limits"]!.AsObject().Remove("max_prompt_tokens"));
        Assert.Equal(
            Find(reviewedBaseline.Models, "gpt-5.4").GetProperty("context_window").GetInt32(),
            Find(Project(missing).Models, "gpt-5.4").GetProperty("context_window").GetInt32());

        var inconsistent = Mutate(snapshot, "gpt-5.5", model =>
            model["capabilities"]!["limits"]!["max_prompt_tokens"] = 1_050_001);
        Assert.Equal(
            Find(reviewedBaseline.Models, "gpt-5.5").GetProperty("context_window").GetInt32(),
            Find(Project(inconsistent).Models, "gpt-5.5").GetProperty("context_window").GetInt32());

        var missingOutput = Mutate(snapshot, "gpt-5.6-luna", model =>
            model["capabilities"]!["limits"]!.AsObject().Remove("max_output_tokens"));
        Assert.Equal(
            Find(reviewedBaseline.Models, "gpt-5.6-luna").GetProperty("context_window").GetInt32(),
            Find(Project(missingOutput).Models, "gpt-5.6-luna").GetProperty("context_window").GetInt32());

        var retired = snapshot.Where(model => model.Id != "gpt-5.6-sol").ToArray();
        var retiredEntry = Find(Project(retired).Models, "gpt-5.6-sol");
        Assert.False(retiredEntry.GetProperty("supported_in_api").GetBoolean());
        Assert.Equal("hide", retiredEntry.GetProperty("visibility").GetString());

        var future = snapshot.Concat([new CopilotModel
        {
            Id = "gpt-future-unknown",
            SupportedEndpoints = ["/responses"],
            Capabilities = new CopilotModelCapabilities
            {
                Limits = new CopilotModelLimits
                {
                    MaxContextWindowTokens = 2_000_000,
                    MaxPromptTokens = 1_800_000,
                    MaxOutputTokens = 200_000,
                },
            },
        }]).ToArray();
        Assert.DoesNotContain(Project(future).Models,
            model => model.GetProperty("slug").GetString() == "gpt-future-unknown");
    }

    private static IReadOnlyList<CopilotModel> LoadSnapshot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SnapshotPath()));
        var envelope = "{\"data\":" + document.RootElement.GetProperty("data").GetRawText() + "}";
        return JsonSerializer.Deserialize(envelope, JsonContext.Default.CopilotModelsResponse)!.Data;
    }

    private static IReadOnlyList<CopilotModel> Mutate(
        IReadOnlyList<CopilotModel> source,
        string slug,
        Action<JsonObject> mutation)
    {
        var envelope = JsonSerializer.Serialize(
            new CopilotModelsResponse { Data = source }, JsonContext.Default.CopilotModelsResponse);
        var root = JsonNode.Parse(envelope)!.AsObject();
        var model = root["data"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["id"]!.GetValue<string>() == slug);
        mutation(model);
        return JsonSerializer.Deserialize(root.ToJsonString(), JsonContext.Default.CopilotModelsResponse)!.Data;
    }

    private static CodexCatalogProjection Project(IReadOnlyList<CopilotModel> live)
    {
        var baseline = CodexCatalogTestFixtures.Load();
        return new CodexCatalogProjector(
            new CodexModelProfileCatalog(),
            new CopilotModelRegistry(),
            NullLogger<CodexCatalogProjector>.Instance)
            .Project(baseline, live, liveOverlayValidated: true);
    }

    private static JsonElement Find(IReadOnlyList<JsonElement> models, string slug) =>
        models.Single(model => model.GetProperty("slug").GetString() == slug);

    private static string SnapshotPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "copilot-codex-model-capabilities-snapshot.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate the committed Copilot Codex model capability snapshot.");
    }
}

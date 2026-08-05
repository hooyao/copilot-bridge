using System.Text.Json;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Models.Codex;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: a remote Codex entry replaces the client's complete same-slug
/// entry. Therefore the bridge must start with the reviewed release catalog,
/// change only backend-owned facts, and fail closed whenever the version,
/// route, live endpoint, or limits do not prove an uplift is safe.
/// </summary>
public sealed class CodexModelCatalogContractTests
{
    private static readonly HashSet<string> BackendOwnedProperties = new(StringComparer.Ordinal)
    {
        "supported_in_api",
        "visibility",
        "context_window",
        "max_context_window",
        "auto_compact_token_limit",
        "auto_review_model_override",
    };

    [Fact]
    public void CapturedOfficialFixtureIsACompleteExactVersionBaseline()
    {
        var baseline = LoadBaseline();

        Assert.Equal("0.144.1", baseline.SourceVersion);
        Assert.Matches("^[0-9a-f]{64}$", baseline.SourceDigest);
        Assert.Equal(8, baseline.Models.Count);
        Assert.All(baseline.Models, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("slug").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("base_instructions").GetString()));
        });
    }

    [Fact]
    public void ValidUpliftPreservesEveryCodexOwnedProperty()
    {
        var baseline = LoadBaseline();
        var before = Find(baseline.Models, "gpt-5.4");
        var result = Project(baseline, [Live("gpt-5.4", 1_050_000, 922_000, 128_000)]);
        var after = Find(result.Models, "gpt-5.4");

        foreach (var property in before.EnumerateObject().Where(p => !BackendOwnedProperties.Contains(p.Name)))
        {
            Assert.True(after.TryGetProperty(property.Name, out var actual), $"Projection dropped Codex property '{property.Name}'.");
            Assert.True(JsonElement.DeepEquals(property.Value, actual), $"Projection changed Codex property '{property.Name}'.");
        }
    }

    [Fact]
    public void ExactThreeWayJoinEnablesOnlyBaselineRouteAndLiveResponsesMatches()
    {
        var baseline = LoadBaseline();
        var live = new[]
        {
            Live("gpt-5.4", 1_050_000, 922_000, 128_000),
            Live("GPT-5.5", 1_050_000, 922_000, 128_000), // wrong case is not exact
            Live("gpt-5.2", 1_050_000, 922_000, 128_000), // baseline + live, but no exact bridge profile
            Live("future-gpt", 1_050_000, 922_000, 128_000), // live-only: never synthesize
            Live("gpt-5.3-codex", 400_000, 272_000, 128_000), // route + live, but absent from this baseline
        };

        var result = Project(baseline, live);

        Assert.True(Find(result.Models, "gpt-5.4").GetProperty("supported_in_api").GetBoolean());
        Assert.False(Find(result.Models, "gpt-5.5").GetProperty("supported_in_api").GetBoolean());
        Assert.Equal("hide", Find(result.Models, "gpt-5.5").GetProperty("visibility").GetString());
        Assert.False(Find(result.Models, "gpt-5.2").GetProperty("supported_in_api").GetBoolean());
        Assert.DoesNotContain(result.Models, model => model.GetProperty("slug").GetString() == "future-gpt");
        Assert.DoesNotContain(result.Models, model => model.GetProperty("slug").GetString() == "gpt-5.3-codex");
    }

    [Fact]
    public void OneMillionClassLimitsMapTotalAndCompactBelowPromptCeiling()
    {
        var result = Project(LoadBaseline(), [Live("gpt-5.4", 1_050_000, 922_000, 128_000)]);
        var model = Find(result.Models, "gpt-5.4");

        Assert.Equal(1_050_000, model.GetProperty("context_window").GetInt32());
        Assert.Equal(1_050_000, model.GetProperty("max_context_window").GetInt32());
        Assert.Equal(898_000, model.GetProperty("auto_compact_token_limit").GetInt32());
        Assert.True(model.GetProperty("auto_compact_token_limit").GetInt32() < 922_000);
    }

    [Theory]
    [InlineData(null, 922000)]
    [InlineData(1050000, null)]
    [InlineData(0, 922000)]
    [InlineData(1050000, 0)]
    [InlineData(1050000, 1050001)]
    [InlineData(-1, 1)]
    public void MissingOrInconsistentLimitsNeverRaiseTheReviewedBaseline(int? total, int? prompt)
    {
        var baseline = LoadBaseline();
        var before = Find(baseline.Models, "gpt-5.4");
        var result = Project(baseline, [Live("gpt-5.4", total, prompt, 128_000)]);
        var after = Find(result.Models, "gpt-5.4");

        Assert.True(JsonElement.DeepEquals(before.GetProperty("context_window"), after.GetProperty("context_window")));
        Assert.True(JsonElement.DeepEquals(before.GetProperty("max_context_window"), after.GetProperty("max_context_window")));
        Assert.True(JsonElement.DeepEquals(before.GetProperty("auto_compact_token_limit"), after.GetProperty("auto_compact_token_limit")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(128001)]
    public void MissingOrInconsistentOutputLimitNeverRaisesTheReviewedBaseline(int? output)
    {
        var baseline = LoadBaseline();
        var before = Find(baseline.Models, "gpt-5.4");
        var result = Project(baseline, [Live("gpt-5.4", 1_050_000, 922_000, output)]);
        var after = Find(result.Models, "gpt-5.4");

        Assert.True(JsonElement.DeepEquals(
            before.GetProperty("context_window"), after.GetProperty("context_window")));
        Assert.True(JsonElement.DeepEquals(
            before.GetProperty("auto_compact_token_limit"),
            after.GetProperty("auto_compact_token_limit")));
    }

    [Fact]
    public void UnvalidatedLiveOverlayKeepsReviewedSafeBaselineWithoutAdvertisingAnUplift()
    {
        var baseline = LoadBaseline();
        var projector = new CodexCatalogProjector(
            new CodexModelProfileCatalog(),
            new CopilotModelRegistry(),
            NullLogger<CodexCatalogProjector>.Instance);

        var result = projector.Project(baseline, [], liveOverlayValidated: false);
        var model = Find(result.Models, "gpt-5.6-sol");

        Assert.True(model.GetProperty("supported_in_api").GetBoolean());
        Assert.Equal(372_000, model.GetProperty("context_window").GetInt32());
        Assert.Equal(JsonValueKind.Null, model.GetProperty("auto_compact_token_limit").ValueKind);
    }

    [Fact]
    public void BaselineValidatorRejectsIncompleteOrMismatchedSourceMetadata()
    {
        var baseline = LoadBaseline();
        var valid = baseline.CacheMetadata!;
        var corrupt = new[]
        {
            valid with { ClientVersion = "0.145.0" },
            valid with { SourceUrl = "https://example.invalid/models.json" },
            valid with { Sha256 = "not-a-digest" },
            valid with { FetchedAtUtc = default },
            valid with { ValidatedAtUtc = default },
        };

        Assert.All(corrupt, metadata => Assert.Throws<InvalidDataException>(
            () => CodexCatalogBaselineValidator.Validate(baseline with { CacheMetadata = metadata })));
    }

    [Fact]
    public void ReviewOverrideIsClearedWhenItsTargetIsNotEffective()
    {
        var baseline = SyntheticBaseline("""
          {"models":[
            {"slug":"reviewer","base_instructions":"review","context_window":100,"max_context_window":100,"auto_compact_token_limit":90,"supported_in_api":true,"visibility":"list","auto_review_model_override":"review-target"},
            {"slug":"review-target","base_instructions":"target","context_window":100,"max_context_window":100,"auto_compact_token_limit":90,"supported_in_api":true,"visibility":"list","auto_review_model_override":null}
          ]}
          """);
        var profiles = new CodexModelProfileCatalog([
            new CodexModelProfile { CanonicalId = "reviewer", AcceptedEfforts = ["low"], DefaultEffort = "low" },
            new CodexModelProfile { CanonicalId = "review-target", AcceptedEfforts = ["low"], DefaultEffort = "low" },
        ]);
        var projector = new CodexCatalogProjector(
            profiles, new AllResponsesRegistry(), NullLogger<CodexCatalogProjector>.Instance);

        var result = projector.Project(baseline, [Live("reviewer", 100, 90, 10)], liveOverlayValidated: true);

        Assert.Equal(JsonValueKind.Null, Find(result.Models, "reviewer").GetProperty("auto_review_model_override").ValueKind);
    }

    [Fact]
    public void StableEffectiveFactsProduceStableEtagAndChangedFactsProduceNewEtag()
    {
        var baseline = LoadBaseline();
        var one = Project(baseline, [Live("gpt-5.4", 1_050_000, 922_000, 128_000)]);
        var same = Project(baseline, [Live("gpt-5.4", 1_050_000, 922_000, 128_000)]);
        var changed = Project(baseline, [Live("gpt-5.4", 1_000_000, 900_000, 100_000)]);

        Assert.Equal(one.ETag, same.ETag);
        Assert.NotEqual(one.ETag, changed.ETag);
        Assert.Matches("^\"[0-9a-f]{64}\"$", one.ETag);
    }

    [Fact]
    public void ProfileAndLiveMetadataCannotEnableAModelWithoutAnExactResponsesRoute()
    {
        var baseline = LoadBaseline();
        var projector = new CodexCatalogProjector(
            new CodexModelProfileCatalog(),
            new NonResponsesRegistry(),
            NullLogger<CodexCatalogProjector>.Instance);

        var result = projector.Project(
            baseline,
            [Live("gpt-5.4", 1_050_000, 922_000, 128_000)],
            liveOverlayValidated: true);
        var model = Find(result.Models, "gpt-5.4");

        Assert.False(model.GetProperty("supported_in_api").GetBoolean());
        Assert.Equal("hide", model.GetProperty("visibility").GetString());
    }

    [Fact]
    public void ResponsesRouteToAnotherSlugCannotEnableTheRequestedModel()
    {
        var baseline = LoadBaseline();
        var projector = new CodexCatalogProjector(
            new CodexModelProfileCatalog(),
            new AliasingResponsesRegistry(),
            NullLogger<CodexCatalogProjector>.Instance);

        var result = projector.Project(
            baseline,
            [Live("gpt-5.4", 1_050_000, 922_000, 128_000)],
            liveOverlayValidated: true);
        var model = Find(result.Models, "gpt-5.4");

        Assert.False(model.GetProperty("supported_in_api").GetBoolean());
        Assert.Equal("hide", model.GetProperty("visibility").GetString());
    }

    private static CodexCatalogBaseline LoadBaseline() => CodexCatalogTestFixtures.LoadCapturedBaseline();

    private static CodexCatalogProjection Project(CodexCatalogBaseline baseline, IReadOnlyList<CopilotModel> live) =>
        new CodexCatalogProjector(
            new CodexModelProfileCatalog(),
            new CopilotModelRegistry(),
            NullLogger<CodexCatalogProjector>.Instance)
            .Project(baseline, live, liveOverlayValidated: true);

    private static CodexCatalogBaseline SyntheticBaseline(string json) =>
        CodexCatalogBaseline.Parse(System.Text.Encoding.UTF8.GetBytes(json), new CodexCatalogCacheMetadata
        {
            SchemaVersion = 1,
            ClientVersion = "0.144.1",
            SourceUrl = "https://raw.githubusercontent.com/openai/codex/rust-v0.144.1/codex-rs/models-manager/models.json",
            Sha256 = new string('b', 64),
            FetchedAtUtc = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            ValidatedAtUtc = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        });

    private static JsonElement Find(IReadOnlyList<JsonElement> models, string slug) =>
        models.Single(model => model.GetProperty("slug").GetString() == slug);

    private static CopilotModel Live(string id, int? total, int? prompt, int? output) => new()
    {
        Id = id,
        SupportedEndpoints = ["/responses"],
        Capabilities = new CopilotModelCapabilities
        {
            Limits = new CopilotModelLimits
            {
                MaxContextWindowTokens = total,
                MaxPromptTokens = prompt,
                MaxOutputTokens = output,
            },
        },
    };

    private sealed class AllResponsesRegistry : IModelRegistry
    {
        public RouteTarget Resolve(string requestedModelId) =>
            new(BackendVendor.CopilotResponses, "/responses", requestedModelId);
    }

    private sealed class NonResponsesRegistry : IModelRegistry
    {
        public RouteTarget Resolve(string requestedModelId) =>
            new(BackendVendor.CopilotOpenAi, "/chat/completions", requestedModelId);
    }

    private sealed class AliasingResponsesRegistry : IModelRegistry
    {
        public RouteTarget Resolve(string requestedModelId) =>
            new(BackendVendor.CopilotResponses, "/responses", "different-model");
    }
}

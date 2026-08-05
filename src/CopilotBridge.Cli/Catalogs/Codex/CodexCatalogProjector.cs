using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Catalogs.Codex;

internal sealed record CodexCatalogProjection
{
    public required IReadOnlyList<JsonElement> Models { get; init; }
    public required string ETag { get; init; }
}

internal sealed class CodexCatalogProjector
{
    private const string ResponsesEndpoint = "/responses";
    private readonly CodexModelProfileCatalog _profiles;
    private readonly IModelRegistry _routes;
    private readonly ILogger<CodexCatalogProjector> _log;

    public CodexCatalogProjector(
        CodexModelProfileCatalog profiles,
        IModelRegistry routes,
        ILogger<CodexCatalogProjector> log)
    {
        _profiles = profiles;
        _routes = routes;
        _log = log;
    }

    public CodexCatalogProjection Project(
        CodexCatalogBaseline baseline,
        IReadOnlyList<CopilotModel> liveModels,
        bool liveOverlayValidated)
    {
        var liveById = liveModels
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var effective = baseline.Models
            .Select(model => IsEffective(GetSlug(model), liveById, liveOverlayValidated))
            .ToArray();
        var effectiveSlugs = baseline.Models
            .Where((_, index) => effective[index])
            .Select(GetSlug)
            .ToHashSet(StringComparer.Ordinal);

        var output = new JsonElement[baseline.Models.Count];
        for (var index = 0; index < baseline.Models.Count; index++)
        {
            var source = baseline.Models[index];
            var slug = GetSlug(source);
            var replacements = new Dictionary<string, Action<Utf8JsonWriter>>(StringComparer.Ordinal)
            {
                ["supported_in_api"] = writer => writer.WriteBooleanValue(effective[index]),
                ["visibility"] = writer => writer.WriteStringValue(effective[index] ? ReadVisibility(source) : "hide"),
            };

            if (effective[index] && liveById.TryGetValue(slug, out var live) && TryMapLimits(live, out var total, out var compact))
            {
                replacements["context_window"] = writer => writer.WriteNumberValue(total);
                replacements["max_context_window"] = writer => writer.WriteNumberValue(total);
                replacements["auto_compact_token_limit"] = writer => writer.WriteNumberValue(compact);
            }
            else if (effective[index])
            {
                _log.LogWarning("Codex catalog model {Model} retained reviewed limits because live Copilot limits were missing or inconsistent.", slug);
            }

            if (source.TryGetProperty("auto_review_model_override", out var review) && review.ValueKind == JsonValueKind.String &&
                !effectiveSlugs.Contains(review.GetString()!))
                replacements["auto_review_model_override"] = writer => writer.WriteNullValue();

            output[index] = RewriteObject(source, replacements);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_tag", baseline.Provenance.SourceTag);
            writer.WriteString("source_commit", baseline.Provenance.SourceCommit);
            writer.WritePropertyName("models");
            writer.WriteStartArray();
            foreach (var model in output) model.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        return new CodexCatalogProjection { Models = output, ETag = $"\"{hash}\"" };
    }

    private bool IsEffective(string slug, IReadOnlyDictionary<string, CopilotModel> live, bool validated)
    {
        var route = _routes.Resolve(slug);
        return _profiles.Get(slug) is not null &&
            route is
            {
                Vendor: BackendVendor.CopilotResponses,
                Endpoint: ResponsesEndpoint,
            } &&
            string.Equals(route.ModelId, slug, StringComparison.Ordinal) &&
            (!validated || live.TryGetValue(slug, out var model) &&
                model.SupportedEndpoints?.Contains(ResponsesEndpoint, StringComparer.Ordinal) == true);
    }

    private static bool TryMapLimits(CopilotModel model, out int total, out int compact)
    {
        total = 0;
        compact = 0;
        var limits = model.Capabilities?.Limits;
        if (limits?.MaxContextWindowTokens is not > 0 || limits.MaxPromptTokens is not > 0 ||
            limits.MaxOutputTokens is not > 0 ||
            (long)limits.MaxPromptTokens.Value + limits.MaxOutputTokens.Value > limits.MaxContextWindowTokens.Value)
            return false;
        total = limits.MaxContextWindowTokens.Value;
        var totalPolicy = total * 9L / 10L;
        var promptPolicy = limits.MaxPromptTokens.Value * 975L / 1000L;
        compact = checked((int)(Math.Min(totalPolicy, promptPolicy) / 1000L * 1000L));
        return compact > 0 && compact < limits.MaxPromptTokens.Value;
    }

    private static JsonElement RewriteObject(JsonElement source, IReadOnlyDictionary<string, Action<Utf8JsonWriter>> replacements)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in source.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (replacements.TryGetValue(property.Name, out var replacement))
                {
                    replacement(writer);
                    written.Add(property.Name);
                }
                else property.Value.WriteTo(writer);
            }
            foreach (var (name, replacement) in replacements)
            {
                if (written.Contains(name) || source.TryGetProperty(name, out _)) continue;
                writer.WritePropertyName(name);
                replacement(writer);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static string GetSlug(JsonElement model) => model.GetProperty("slug").GetString()!;
    private static string ReadVisibility(JsonElement model) =>
        model.TryGetProperty("visibility", out var visibility) && visibility.ValueKind == JsonValueKind.String
            ? visibility.GetString()!
            : "hide";
}

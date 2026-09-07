using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly RoutesConfig _configuredRoutes;
    private readonly ILogger<CodexCatalogProjector> _log;

    public CodexCatalogProjector(
        CodexModelProfileCatalog profiles,
        IModelRegistry routes,
        ILogger<CodexCatalogProjector> log)
        : this(
            profiles,
            routes,
            Options.Create(new RoutesConfig()),
            log)
    {
    }

    public CodexCatalogProjector(
        CodexModelProfileCatalog profiles,
        IModelRegistry routes,
        IOptions<RoutesConfig> configuredRoutes,
        ILogger<CodexCatalogProjector> log)
    {
        _profiles = profiles;
        _routes = routes;
        _configuredRoutes = configuredRoutes.Value;
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
        var resolvedRoutes = baseline.Models
            .Select(model => ResolveConfiguredTarget(GetSlug(model)))
            .ToArray();
        var effective = baseline.Models
            .Select((model, index) => IsEffective(
                GetSlug(model), resolvedRoutes[index], liveById, liveOverlayValidated))
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

            var resolved = resolvedRoutes[index];
            if (effective[index] && resolved.IsInvariant && resolved.Target is { } target &&
                liveById.TryGetValue(target.ModelId, out var live) &&
                TryMapLimits(live, out var total, out var compact))
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
            writer.WriteString("source_version", baseline.SourceVersion);
            writer.WriteString("source_digest", baseline.SourceDigest);
            writer.WritePropertyName("models");
            writer.WriteStartArray();
            foreach (var model in output) model.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        return new CodexCatalogProjection { Models = output, ETag = $"\"{hash}\"" };
    }

    /// <summary>
    /// Resolve a catalog slug only when its target is invariant across every
    /// request carrying that slug. The first Location that could match controls
    /// first-match-wins routing: an unconditional match proves one target, while
    /// an effort/header-dependent match means the catalog cannot safely advertise
    /// any later fallback's capacity.
    /// </summary>
    private CatalogTargetResolution ResolveConfiguredTarget(string slug)
    {
        foreach (var location in _configuredRoutes.Locations)
        {
            switch (EvaluateForModel(location.When, slug))
            {
                case FixedModelMatch.Never:
                    continue;
                case FixedModelMatch.Conditional:
                    return new CatalogTargetResolution(Target: null, IsInvariant: false);
                case FixedModelMatch.Always:
                    var targetId = string.IsNullOrWhiteSpace(location.Use.Model)
                        ? slug
                        : location.Use.Model;
                    return new CatalogTargetResolution(
                        _routes.Resolve(targetId), IsInvariant: true);
                default:
                    throw new InvalidOperationException("Unknown fixed-model route state.");
            }
        }

        return new CatalogTargetResolution(_routes.Resolve(slug), IsInvariant: true);
    }

    private bool IsEffective(
        string slug,
        CatalogTargetResolution resolution,
        IReadOnlyDictionary<string, CopilotModel> live,
        bool validated)
    {
        var route = resolution.Target;
        if (!resolution.IsInvariant)
        {
            if (validated)
            {
                _log.LogWarning(
                    "Codex catalog model {Model} hidden because an earlier request-dependent "
                    + "Routing.Location can resolve it to different targets.",
                    slug);
                return false;
            }

            // A missing live overlay already falls back to the reviewed source
            // catalog. Preserve that degradation contract without claiming any
            // request-dependent target capacity.
            route = _routes.Resolve(slug);
        }

        return _profiles.Get(slug) is not null &&
            route is not null &&
            _profiles.Get(route.ModelId) is not null &&
            route is
            {
                Vendor: BackendVendor.CopilotResponses,
                Endpoint: ResponsesEndpoint,
            } &&
            (!validated || live.TryGetValue(route.ModelId, out var model) &&
                model.SupportedEndpoints?.Contains(ResponsesEndpoint, StringComparer.Ordinal) == true);
    }

    /// <summary>
    /// Evaluate a match tree with the model fixed and all request-dependent axes
    /// unknown. The result is deliberately conservative: correlations between
    /// effort/header leaves that are not provably impossible remain Conditional.
    /// </summary>
    private static FixedModelMatch EvaluateForModel(MatchExpression expression, string model)
    {
        var result = FixedModelMatch.Always;

        if (expression.AllOf is { Count: > 0 } all)
        {
            var allResult = FixedModelMatch.Always;
            foreach (var child in all)
                allResult = And(allResult, EvaluateForModel(child, model));
            result = And(result, allResult);
        }

        if (expression.AnyOf is { Count: > 0 } any)
        {
            var anyResult = FixedModelMatch.Never;
            foreach (var child in any)
                anyResult = Or(anyResult, EvaluateForModel(child, model));
            result = And(result, anyResult);
        }

        if (expression.Model is { Length: > 0 } expectedModel)
        {
            result = And(
                result,
                string.Equals(expectedModel, model, StringComparison.OrdinalIgnoreCase)
                    ? FixedModelMatch.Always
                    : FixedModelMatch.Never);
        }

        if (expression.Effort is { Length: > 0 })
            result = And(result, FixedModelMatch.Conditional);
        if (expression.Header is not null)
            result = And(result, FixedModelMatch.Conditional);

        return result;
    }

    private static FixedModelMatch And(FixedModelMatch left, FixedModelMatch right) =>
        left is FixedModelMatch.Never || right is FixedModelMatch.Never
            ? FixedModelMatch.Never
            : left is FixedModelMatch.Always && right is FixedModelMatch.Always
                ? FixedModelMatch.Always
                : FixedModelMatch.Conditional;

    private static FixedModelMatch Or(FixedModelMatch left, FixedModelMatch right) =>
        left is FixedModelMatch.Always || right is FixedModelMatch.Always
            ? FixedModelMatch.Always
            : left is FixedModelMatch.Never && right is FixedModelMatch.Never
                ? FixedModelMatch.Never
                : FixedModelMatch.Conditional;

    private readonly record struct CatalogTargetResolution(RouteTarget? Target, bool IsInvariant);

    private enum FixedModelMatch
    {
        Never,
        Conditional,
        Always,
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
        var totalPolicy = total * 85L / 100L;
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

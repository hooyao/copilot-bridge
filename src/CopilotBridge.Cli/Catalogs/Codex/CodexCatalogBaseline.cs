using System.Net.Http.Headers;
using System.Text.Json;
using CopilotBridge.Cli.Models.Codex;

namespace CopilotBridge.Cli.Catalogs.Codex;

internal sealed record CodexCatalogBaseline
{
    public required CodexCatalogCacheMetadata CacheMetadata { get; init; }
    public required IReadOnlyList<JsonElement> Models { get; init; }

    public string SourceVersion => CacheMetadata.ClientVersion;

    public string SourceDigest => CacheMetadata.Sha256;

    public static CodexCatalogBaseline Parse(ReadOnlyMemory<byte> json, CodexCatalogCacheMetadata metadata)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex catalog must contain a top-level models array.");
        return new CodexCatalogBaseline
        {
            CacheMetadata = metadata,
            Models = models.EnumerateArray().Select(model => model.Clone()).ToArray(),
        };
    }
}

internal readonly record struct CodexClientVersion
{
    private readonly string? _canonical;

    private CodexClientVersion(string canonical) => _canonical = canonical;

    public static bool TryParse(string? value, out CodexClientVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || value.Any(ch => !char.IsAscii(ch) || char.IsWhiteSpace(ch) || char.IsControl(ch)))
            return false;

        var buildSeparator = value.IndexOf('+');
        if (buildSeparator >= 0 &&
            (value.LastIndexOf('+') != buildSeparator ||
             !ValidIdentifiers(value[(buildSeparator + 1)..], numericLeadingZeroAllowed: true)))
            return false;
        var withoutBuild = buildSeparator >= 0 ? value[..buildSeparator] : value;

        var prereleaseSeparator = withoutBuild.IndexOf('-');
        if (prereleaseSeparator >= 0 &&
            !ValidIdentifiers(withoutBuild[(prereleaseSeparator + 1)..], numericLeadingZeroAllowed: false))
            return false;
        var coreText = prereleaseSeparator >= 0 ? withoutBuild[..prereleaseSeparator] : withoutBuild;
        var core = coreText.Split('.');
        if (core.Length != 3 || core.Any(piece => !ValidCore(piece)))
            return false;

        version = new CodexClientVersion(value);
        return true;
    }

    private static bool ValidCore(string value) =>
        value.Length > 0 && (value.Length == 1 || value[0] != '0') &&
        value.All(char.IsAsciiDigit) && int.TryParse(value, out _);

    private static bool ValidIdentifiers(string value, bool numericLeadingZeroAllowed)
    {
        if (value.Length == 0) return false;
        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0 || part.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-')) return false;
            if (!numericLeadingZeroAllowed && part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit)) return false;
        }
        return true;
    }

    public override string ToString() => _canonical ?? "0.0.0";
}

internal static class CodexCatalogRequestIdentity
{
    private const string DesktopPrefix = "Codex Desktop/";
    private const string CliPrefix = "codex_cli_rs/";
    private const string ExecPrefix = "codex_exec/";

    public static bool TryResolve(
        string? queryVersion,
        string? userAgent,
        out CodexClientVersion version,
        out string? error)
    {
        version = default;
        error = null;
        if (!CodexClientVersion.TryParse(queryVersion, out var query))
        {
            error = "client_version must be one canonical semantic version.";
            return false;
        }

        if (!TryReadCodexUserAgentVersion(userAgent, out var hasCodexIdentity, out var userAgentVersion))
        {
            error = "Codex User-Agent contains an invalid client version.";
            return false;
        }
        if (!hasCodexIdentity)
        {
            version = query;
            return true;
        }

        var queryCore = Core(query.ToString());
        var userAgentCore = Core(userAgentVersion.ToString());
        if (!string.Equals(queryCore, userAgentCore, StringComparison.Ordinal))
        {
            error = "client_version does not match the Codex User-Agent version.";
            return false;
        }
        if (query.ToString().Contains('-', StringComparison.Ordinal) ||
            query.ToString().Contains('+', StringComparison.Ordinal))
        {
            if (!query.Equals(userAgentVersion))
            {
                error = "complete client_version does not match the Codex User-Agent version.";
                return false;
            }
            version = query;
            return true;
        }

        version = userAgentVersion;
        return true;
    }

    private static bool TryReadCodexUserAgentVersion(
        string? userAgent,
        out bool hasCodexIdentity,
        out CodexClientVersion version)
    {
        hasCodexIdentity = false;
        version = default;
        if (string.IsNullOrEmpty(userAgent)) return true;
        var prefix = userAgent.StartsWith(DesktopPrefix, StringComparison.Ordinal)
            ? DesktopPrefix
            : userAgent.StartsWith(CliPrefix, StringComparison.Ordinal)
                ? CliPrefix
                : userAgent.StartsWith(ExecPrefix, StringComparison.Ordinal)
                    ? ExecPrefix
                    : null;
        if (prefix is null) return true;
        hasCodexIdentity = true;
        var separator = userAgent.IndexOf(' ', prefix.Length);
        var text = separator < 0 ? userAgent[prefix.Length..] : userAgent[prefix.Length..separator];
        return CodexClientVersion.TryParse(text, out version);
    }

    private static string Core(string value)
    {
        var end = value.IndexOfAny(['-', '+']);
        return end < 0 ? value : value[..end];
    }
}

internal static class CodexCatalogBaselineValidator
{
    public static void Validate(CodexCatalogBaseline baseline)
    {
        var metadata = baseline.CacheMetadata;
        if (metadata.SchemaVersion != 1 ||
            !CodexClientVersion.TryParse(metadata.ClientVersion, out var version) ||
            metadata.SourceUrl != CodexCatalogSource.BuildUri(version).AbsoluteUri ||
            !IsHex(metadata.Sha256, 64) || metadata.FetchedAtUtc == default || metadata.ValidatedAtUtc == default ||
            metadata.ValidatedAtUtc < metadata.FetchedAtUtc ||
            metadata.SourceETag is { } etag && !EntityTagHeaderValue.TryParse(etag, out _))
            throw new InvalidDataException("Codex catalog source metadata is incomplete.");
        if (baseline.Models.Count == 0)
            throw new InvalidDataException("Codex catalog must contain at least one model.");

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var overrides = new List<(string Slug, string Target)>();
        foreach (var model in baseline.Models)
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("slug", out var slugProperty) ||
                slugProperty.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(slugProperty.GetString()))
                throw new InvalidDataException("Every Codex catalog entry must have a non-empty slug.");
            var slug = slugProperty.GetString()!;
            if (!slugs.Add(slug)) throw new InvalidDataException($"Duplicate Codex catalog slug '{slug}'.");
            if (!model.TryGetProperty("base_instructions", out var instructions) ||
                instructions.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(instructions.GetString()))
                throw new InvalidDataException($"Codex catalog entry '{slug}' has no instruction source.");
            if (!TryReadPositiveInt(model, "context_window", out var contextWindow) ||
                !TryReadPositiveInt(model, "max_context_window", out var maximumWindow) || maximumWindow < contextWindow)
                throw new InvalidDataException($"Codex catalog entry '{slug}' has invalid context-window fields.");
            if (!model.TryGetProperty("auto_compact_token_limit", out var compact) ||
                compact.ValueKind != JsonValueKind.Null &&
                (compact.ValueKind != JsonValueKind.Number || !compact.TryGetInt32(out var compactValue) ||
                 compactValue <= 0 || compactValue >= contextWindow))
                throw new InvalidDataException($"Codex catalog entry '{slug}' has invalid compaction behavior.");
            if (!model.TryGetProperty("supported_in_api", out var supported) ||
                supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Codex catalog entry '{slug}' has invalid API support metadata.");
            if (!model.TryGetProperty("visibility", out var visibility) ||
                visibility.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(visibility.GetString()))
                throw new InvalidDataException($"Codex catalog entry '{slug}' has invalid visibility metadata.");
            if (model.TryGetProperty("auto_review_model_override", out var review))
            {
                if (review.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(review.GetString()))
                    overrides.Add((slug, review.GetString()!));
                else if (review.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException($"Codex catalog entry '{slug}' has invalid review override metadata.");
            }
        }
        foreach (var (slug, target) in overrides)
            if (!slugs.Contains(target)) throw new InvalidDataException($"Codex catalog entry '{slug}' has unknown review override '{target}'.");
    }

    private static bool IsHex(string? value, int expectedLength) =>
        value is { } && value.Length == expectedLength &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool TryReadPositiveInt(JsonElement model, string name, out int value)
    {
        value = 0;
        return model.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value) && value > 0;
    }
}

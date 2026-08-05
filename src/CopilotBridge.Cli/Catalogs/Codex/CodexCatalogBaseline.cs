using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Codex;

namespace CopilotBridge.Cli.Catalogs.Codex;

internal sealed record CodexCatalogBaseline
{
    public required CodexCatalogProvenance Provenance { get; init; }
    public required IReadOnlyList<JsonElement> Models { get; init; }

    public static CodexCatalogBaseline Parse(string json, CodexCatalogProvenance provenance)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex catalog must contain a top-level models array.");

        return new CodexCatalogBaseline
        {
            Provenance = provenance,
            Models = models.EnumerateArray().Select(model => model.Clone()).ToArray(),
        };
    }
}

internal sealed class CodexCatalogBaselineStore
{
    private const string ResourcePrefix = "CopilotBridge.Cli.Catalogs.Codex.0.144.";
    private readonly CodexCatalogBaseline _baseline;
    private readonly CodexClientVersion _minimum;
    private readonly CodexClientVersion _maximum;

    public CodexCatalogBaselineStore()
    {
        var assembly = typeof(CodexCatalogBaselineStore).Assembly;
        var provenanceJson = ReadResource(assembly, ResourcePrefix + "provenance.json");
        var provenance = JsonSerializer.Deserialize(provenanceJson, JsonContext.Default.CodexCatalogProvenance)
            ?? throw new InvalidDataException("Embedded Codex catalog provenance is empty.");
        var modelsJson = ReadResource(assembly, ResourcePrefix + "models.json");
        var license = ReadResourceBytes(assembly, ResourcePrefix + "LICENSE.openai-codex");

        if (!string.Equals(Sha256(modelsJson), provenance.ModelsSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(license)), provenance.LicenseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Embedded Codex catalog bytes do not match provenance hashes.");

        _minimum = CodexClientVersion.ParseReviewed(provenance.SupportedClientVersion.MinimumInclusive);
        _maximum = CodexClientVersion.ParseReviewed(provenance.SupportedClientVersion.MaximumExclusive);
        _baseline = CodexCatalogBaseline.Parse(modelsJson, provenance);
        CodexCatalogBaselineValidator.Validate(_baseline);
    }

    public bool TryGet(string? clientVersion, out CodexCatalogBaseline? baseline, out string error)
    {
        baseline = null;
        if (!CodexClientVersion.TryParse(clientVersion, out var parsed))
        {
            error = "client_version must be a semantic version such as 0.144.1.";
            return false;
        }
        if (parsed.CompareTo(_minimum) < 0 || parsed.CompareTo(_maximum) >= 0)
        {
            error = $"client_version '{clientVersion}' is outside the reviewed interval [{_minimum}, {_maximum}).";
            return false;
        }
        baseline = _baseline;
        error = "";
        return true;
    }

    private static string ReadResource(Assembly assembly, string name) =>
        System.Text.Encoding.UTF8.GetString(ReadResourceBytes(assembly, name));

    private static byte[] ReadResourceBytes(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Embedded Codex catalog resource is missing: {name}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

internal readonly record struct CodexClientVersion(int Major, int Minor, int Patch) : IComparable<CodexClientVersion>
{
    public static bool TryParse(string? value, out CodexClientVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var core = value.Split(['-', '+'], 2)[0];
        var pieces = core.Split('.');
        if (pieces.Length != 3 || pieces.Any(piece => piece.Length == 0 || piece.Any(ch => !char.IsAsciiDigit(ch))))
            return false;
        if (!int.TryParse(pieces[0], out var major) || !int.TryParse(pieces[1], out var minor) || !int.TryParse(pieces[2], out var patch))
            return false;
        version = new CodexClientVersion(major, minor, patch);
        return true;
    }

    public static CodexClientVersion ParseReviewed(string value) =>
        TryParse(value, out var version) ? version : throw new InvalidDataException($"Invalid reviewed Codex version '{value}'.");

    public int CompareTo(CodexClientVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

internal static class CodexCatalogBaselineValidator
{
    private const string ExpectedRepository = "https://github.com/openai/codex";
    private const string ExpectedSourcePath = "codex-rs/models-manager/models.json";
    private const string ExpectedLicense = "Apache-2.0";
    private const string SourceTagPrefix = "rust-v";

    public static void Validate(CodexCatalogBaseline baseline)
    {
        var provenance = baseline.Provenance;
        if (provenance.SchemaVersion != 1 ||
            !string.Equals(provenance.SourceRepository, ExpectedRepository, StringComparison.Ordinal) ||
            !string.Equals(provenance.SourcePath, ExpectedSourcePath, StringComparison.Ordinal) ||
            !string.Equals(provenance.SourceLicense, ExpectedLicense, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(provenance.LicenseAsset) ||
            !IsHex(provenance.SourceCommit, 40) ||
            !IsHex(provenance.ModelsSha256, 64) ||
            !IsHex(provenance.LicenseSha256, 64) ||
            !TryParseSourceTag(provenance.SourceTag, out var sourceVersion) ||
            provenance.SupportedClientVersion is null ||
            !CodexClientVersion.TryParse(provenance.SupportedClientVersion.MinimumInclusive, out var minimum) ||
            !CodexClientVersion.TryParse(provenance.SupportedClientVersion.MaximumExclusive, out var maximum) ||
            minimum.CompareTo(maximum) >= 0 || sourceVersion.CompareTo(minimum) < 0 ||
            sourceVersion.CompareTo(maximum) >= 0)
            throw new InvalidDataException("Codex catalog provenance is incomplete.");

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var overrides = new List<(string Slug, string Target)>();
        foreach (var model in baseline.Models)
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("slug", out var slugProperty) ||
                string.IsNullOrWhiteSpace(slugProperty.GetString()))
                throw new InvalidDataException("Every Codex catalog entry must have a non-empty slug.");
            var slug = slugProperty.GetString()!;
            if (!slugs.Add(slug)) throw new InvalidDataException($"Duplicate Codex catalog slug '{slug}'.");
            if (!model.TryGetProperty("base_instructions", out var instructions) || string.IsNullOrWhiteSpace(instructions.GetString()))
                throw new InvalidDataException($"Codex catalog entry '{slug}' has no instruction source.");
            foreach (var required in new[] { "context_window", "max_context_window", "supported_in_api", "visibility" })
                if (!model.TryGetProperty(required, out _)) throw new InvalidDataException($"Codex catalog entry '{slug}' lacks '{required}'.");
            if (model.TryGetProperty("auto_review_model_override", out var review) && review.ValueKind == JsonValueKind.String)
                overrides.Add((slug, review.GetString()!));
        }
        foreach (var (slug, target) in overrides)
            if (!slugs.Contains(target)) throw new InvalidDataException($"Codex catalog entry '{slug}' has unknown review override '{target}'.");
    }

    private static bool TryParseSourceTag(string? sourceTag, out CodexClientVersion version)
    {
        version = default;
        if (sourceTag is null || !sourceTag.StartsWith(SourceTagPrefix, StringComparison.Ordinal)) return false;
        var versionText = sourceTag[SourceTagPrefix.Length..];
        return CodexClientVersion.TryParse(versionText, out version) &&
            string.Equals(version.ToString(), versionText, StringComparison.Ordinal);
    }

    private static bool IsHex(string? value, int expectedLength) =>
        value is { } && value.Length == expectedLength &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

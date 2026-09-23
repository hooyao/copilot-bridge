using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotBridge.Cli.Catalogs.Codex;

/// <summary>
/// Reviewed complete Codex model resources that may post-date the requesting
/// client's own catalog. These are client-owned records captured byte-for-byte
/// from one pinned official <c>openai/codex</c> revision; the projector uses them
/// only for requests older than their declared minimum client version and when
/// the exact slug is absent from the selected baseline.
/// </summary>
internal sealed class CodexSupplementalCatalog
{
    private const string ModelsResourceName = "CopilotBridge.Cli.Catalogs.Codex.Supplemental.models.json";
    private const string CaptureResourceName = "CopilotBridge.Cli.Catalogs.Codex.Supplemental.capture.json";
    private static readonly string[] ReviewedSlugs = ["gpt-6-luna", "gpt-6-sol"];

    private CodexSupplementalCatalog(
        IReadOnlyList<JsonElement> models,
        CodexClientVersion minimumClientVersion)
    {
        Models = models;
        MinimumClientVersion = minimumClientVersion;
    }

    public IReadOnlyList<JsonElement> Models { get; }
    public CodexClientVersion MinimumClientVersion { get; }

    public static CodexSupplementalCatalog Load()
    {
        var assembly = typeof(CodexSupplementalCatalog).Assembly;
        var modelBytes = ReadResource(assembly, ModelsResourceName);
        using var capture = JsonDocument.Parse(ReadResource(assembly, CaptureResourceName));
        var recordedDigest = capture.RootElement.GetProperty("resource_sha256").GetString()
            ?? throw new InvalidDataException("Supplemental Codex catalog provenance has no resource digest.");
        return Parse(modelBytes, recordedDigest);
    }

    internal static CodexSupplementalCatalog Parse(ReadOnlyMemory<byte> bytes, string expectedDigest)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        if (!string.Equals(digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Supplemental Codex catalog digest {digest} does not match its recorded provenance {expectedDigest}.");

        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("models", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Supplemental Codex catalog must contain a top-level models array.");

        var models = array.EnumerateArray().Select(model => model.Clone()).ToArray();
        CodexCatalogBaselineValidator.ValidateModels(models);
        var actualSlugs = models.Select(model => model.GetProperty("slug").GetString()!).ToArray();
        if (actualSlugs.Length != ReviewedSlugs.Length ||
            !ReviewedSlugs.Order(StringComparer.Ordinal).SequenceEqual(
                actualSlugs.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Supplemental Codex catalog does not contain the exact reviewed model set.");

        var minimumVersions = models
            .Select(model => model.TryGetProperty("minimal_client_version", out var minimum) &&
                             minimum.ValueKind == JsonValueKind.String
                ? minimum.GetString()
                : null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (minimumVersions is not [{ } minimumText] ||
            !CodexClientVersion.TryParse(minimumText, out var minimumVersion))
            throw new InvalidDataException(
                "Supplemental Codex catalog must declare one canonical minimum client version.");

        return new CodexSupplementalCatalog(models, minimumVersion);
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Supplemental Codex catalog resource '{name}' is missing from the assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

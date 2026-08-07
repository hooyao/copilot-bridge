using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Cli.Models.Codex;

namespace CopilotBridge.Cli.Catalogs.Codex;

/// <summary>
/// The single official Codex catalog snapshot compiled into the bridge, served
/// as the projection baseline when a client's exact tag is confirmed absent
/// upstream.
///
/// OpenAI ships Codex client builds before tagging the matching release, so the
/// newest client — the one that most needs the bridge's Copilot-calibrated
/// limits — is the one whose canonical tag is guaranteed to 404 for a while.
/// Before this snapshot existed, that produced a metadata error every few
/// minutes and the client silently kept its own bundled catalog.
///
/// This is NOT tag guessing: the snapshot is one fixed, reviewed artifact whose
/// bytes were verified byte-identical to its canonical source. Nothing here
/// searches or compares available tags, which the catalog spec still forbids.
/// It is also never cached, so a tag published later always wins.
/// </summary>
internal sealed class CodexBundledCatalog
{
    private const string CatalogResourceName = "CopilotBridge.Cli.Catalogs.Codex.Bundled.models.json";
    private const string CaptureResourceName = "CopilotBridge.Cli.Catalogs.Codex.Bundled.capture.json";

    private CodexBundledCatalog(CodexCatalogBaseline baseline) => Baseline = baseline;

    /// <summary>The validated snapshot, carrying the version it was captured from.</summary>
    public CodexCatalogBaseline Baseline { get; }

    /// <summary>The version this snapshot was captured from — never the requested version.</summary>
    public string CapturedVersion => Baseline.CacheMetadata.ClientVersion;

    /// <summary>
    /// Reads, parses, and fully validates the embedded snapshot. A missing,
    /// unparseable, tampered, or invalid resource is a build defect rather than
    /// a runtime condition, so this throws and is called during startup
    /// composition — failing at the first client request instead would hide the
    /// defect until a user hit the fallback path.
    /// </summary>
    public static CodexBundledCatalog Load()
    {
        var assembly = typeof(CodexBundledCatalog).Assembly;
        var catalogBytes = ReadResource(assembly, CatalogResourceName);
        var metadata = ParseCapture(ReadResource(assembly, CaptureResourceName));

        // The recorded digest is checked against the actual embedded bytes so a
        // hand-edited or corrupted snapshot cannot masquerade as the official
        // artifact whose provenance the metadata claims.
        var digest = Convert.ToHexStringLower(SHA256.HashData(catalogBytes));
        if (!string.Equals(digest, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Bundled Codex catalog digest {digest} does not match its recorded provenance {metadata.Sha256}.");

        var baseline = CodexCatalogBaseline.Parse(catalogBytes, metadata);
        CodexCatalogBaselineValidator.Validate(baseline);
        return new CodexBundledCatalog(baseline);
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Bundled Codex catalog resource '{name}' is missing from the assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds cache metadata from the snapshot's capture record. The capture
    /// format records one <c>captured_at_utc</c> instant — the moment the bytes
    /// were verified against their canonical source — which supplies both the
    /// fetch and validation timestamps the shared validator requires. This
    /// mirrors how the test fixtures under tests/Fixtures/Codex are loaded.
    /// </summary>
    private static CodexCatalogCacheMetadata ParseCapture(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var capture = document.RootElement;
        var capturedAt = capture.GetProperty("captured_at_utc").GetDateTimeOffset();
        return new CodexCatalogCacheMetadata
        {
            SchemaVersion = capture.GetProperty("schema_version").GetInt32(),
            ClientVersion = capture.GetProperty("client_version").GetString()
                ?? throw new InvalidDataException("Bundled Codex catalog provenance has no client version."),
            SourceUrl = capture.GetProperty("source_url").GetString()
                ?? throw new InvalidDataException("Bundled Codex catalog provenance has no source URL."),
            SourceETag = capture.TryGetProperty("source_etag", out var etag) && etag.ValueKind == JsonValueKind.String
                ? etag.GetString()
                : null,
            Sha256 = capture.GetProperty("sha256").GetString()
                ?? throw new InvalidDataException("Bundled Codex catalog provenance has no digest."),
            FetchedAtUtc = capturedAt,
            ValidatedAtUtc = capturedAt,
        };
    }
}

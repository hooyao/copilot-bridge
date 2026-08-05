using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Models.Codex;

namespace CopilotBridge.UnitTests;

internal static class CodexCatalogTestFixtures
{
    public const string CapturedVersion = "0.144.1";

    public static CodexCatalogBaseline LoadCapturedBaseline(string exactVersion = CapturedVersion)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Codex", "rust-v" + exactVersion);
        return LoadFromDirectory(directory, exactVersion);
    }

    internal static CodexCatalogBaseline LoadFromDirectory(
        string directory,
        string expectedVersion = CapturedVersion)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, "models.json"));
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "capture.json")));
        var capture = document.RootElement;
        var schemaVersion = capture.GetProperty("schema_version").GetInt32();
        var clientVersion = capture.GetProperty("client_version").GetString();
        var sourceUrl = capture.GetProperty("source_url").GetString();
        var sourceEtag = capture.GetProperty("source_etag").GetString();
        var recordedDigest = capture.GetProperty("sha256").GetString();
        var capturedAt = capture.GetProperty("captured_at_utc").GetDateTimeOffset();
        var actualDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (schemaVersion != 1 || clientVersion != expectedVersion ||
            !CodexClientVersion.TryParse(expectedVersion, out var version) ||
            sourceUrl != CodexCatalogSource.BuildUri(version).AbsoluteUri ||
            !string.Equals(recordedDigest, actualDigest, StringComparison.Ordinal))
            throw new InvalidDataException("Captured Codex fixture does not match its recorded official-source provenance.");

        var metadata = new CodexCatalogCacheMetadata
        {
            SchemaVersion = schemaVersion,
            ClientVersion = clientVersion!,
            SourceUrl = sourceUrl!,
            SourceETag = sourceEtag,
            Sha256 = recordedDigest!,
            FetchedAtUtc = capturedAt,
            ValidatedAtUtc = capturedAt,
        };
        var baseline = CodexCatalogBaseline.Parse(bytes, metadata);
        CodexCatalogBaselineValidator.Validate(baseline);
        return baseline;
    }
}

using System.Security.Cryptography;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Models.Codex;

namespace CopilotBridge.Playground;

internal static class CodexCatalogTestFixtures
{
    public static CodexCatalogBaseline Load(string exactVersion = "0.144.1")
    {
        var bytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Codex", "rust-v" + exactVersion, "models.json"));
        if (!CodexClientVersion.TryParse(exactVersion, out var version))
            throw new InvalidDataException("Captured Codex fixture version is invalid.");
        var timestamp = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var metadata = new CodexCatalogCacheMetadata
        {
            SchemaVersion = 1,
            ClientVersion = exactVersion,
            SourceUrl = CodexCatalogSource.BuildUri(version).AbsoluteUri,
            SourceETag = "\"captured-fixture\"",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            FetchedAtUtc = timestamp,
            ValidatedAtUtc = timestamp,
        };
        var baseline = CodexCatalogBaseline.Parse(bytes, metadata);
        CodexCatalogBaselineValidator.Validate(baseline);
        return baseline;
    }
}

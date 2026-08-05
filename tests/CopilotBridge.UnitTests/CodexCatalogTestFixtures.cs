using System.Security.Cryptography;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Models.Codex;

namespace CopilotBridge.UnitTests;

internal static class CodexCatalogTestFixtures
{
    public const string CapturedVersion = "0.144.1";

    public static CodexCatalogBaseline LoadCapturedBaseline(string exactVersion = CapturedVersion)
    {
        var bytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Codex", "rust-v" + exactVersion, "models.json"));
        if (!CodexClientVersion.TryParse(exactVersion, out var version))
            throw new InvalidDataException("Captured Codex fixture version is invalid.");
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var metadata = new CodexCatalogCacheMetadata
        {
            SchemaVersion = 1,
            ClientVersion = exactVersion,
            SourceUrl = CodexCatalogSource.BuildUri(version).AbsoluteUri,
            SourceETag = "\"captured-fixture\"",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            FetchedAtUtc = now,
            ValidatedAtUtc = now,
        };
        var baseline = CodexCatalogBaseline.Parse(bytes, metadata);
        CodexCatalogBaselineValidator.Validate(baseline);
        return baseline;
    }
}

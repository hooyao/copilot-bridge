using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Codex;

internal sealed record CodexModelsResponse
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<JsonElement> Models { get; init; }
}

internal sealed record CodexCatalogProvenance
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("source_repository")]
    public required string SourceRepository { get; init; }

    [JsonPropertyName("source_tag")]
    public required string SourceTag { get; init; }

    [JsonPropertyName("source_commit")]
    public required string SourceCommit { get; init; }

    [JsonPropertyName("source_path")]
    public required string SourcePath { get; init; }

    [JsonPropertyName("source_license")]
    public required string SourceLicense { get; init; }

    [JsonPropertyName("license_asset")]
    public required string LicenseAsset { get; init; }

    [JsonPropertyName("supported_client_version")]
    public required CodexSupportedClientVersion SupportedClientVersion { get; init; }

    [JsonPropertyName("models_sha256")]
    public required string ModelsSha256 { get; init; }

    [JsonPropertyName("license_sha256")]
    public required string LicenseSha256 { get; init; }
}

internal sealed record CodexSupportedClientVersion
{
    [JsonPropertyName("minimum_inclusive")]
    public required string MinimumInclusive { get; init; }

    [JsonPropertyName("maximum_exclusive")]
    public required string MaximumExclusive { get; init; }
}

internal sealed record CodexCatalogErrorResponse
{
    [JsonPropertyName("error")]
    public required CodexCatalogError Error { get; init; }
}

internal sealed record CodexCatalogError
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

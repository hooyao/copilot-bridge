using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Codex;

internal sealed record CodexModelsResponse
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<JsonElement> Models { get; init; }
}

internal sealed record CodexCatalogCacheMetadata
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("client_version")]
    public required string ClientVersion { get; init; }

    [JsonPropertyName("source_url")]
    public required string SourceUrl { get; init; }

    [JsonPropertyName("source_etag")]
    public string? SourceETag { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("fetched_at_utc")]
    public DateTimeOffset FetchedAtUtc { get; init; }

    [JsonPropertyName("validated_at_utc")]
    public DateTimeOffset ValidatedAtUtc { get; init; }
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

using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.GitHub;

/// <summary>
/// Decrypted payload of the single authoritative github_credentials.dat file.
/// Version selects the complete credential protocol; callers never infer it from
/// the filename or token bytes.
/// </summary>
internal sealed record CredentialFileRecord
{
    public const int CopilotPluginVersion = 1;
    public const int GitHubCliOAuthVersion = 2;

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("access_token_expires_at")]
    public DateTimeOffset? AccessTokenExpiresAt { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("refresh_token_expires_at")]
    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("credential_id")]
    public string CredentialId { get; init; } = "";

    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    [JsonIgnore]
    public bool IsRefreshable => !string.IsNullOrWhiteSpace(RefreshToken);

    [JsonIgnore]
    public bool IsDirect => Version == GitHubCliOAuthVersion;

    public override string ToString() =>
        $"CredentialFileRecord {{ Version = {Version}, AccessToken = (redacted), "
        + $"AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, "
        + $"RefreshToken = {(IsRefreshable ? "(redacted)" : "(none)")}, "
        + $"RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O}, "
        + $"TokenType = {TokenType}, Scope = {Scope}, Generation = {Generation} }}";
}

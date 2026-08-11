using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.GitHub;

/// <summary>
/// Versioned plaintext payload protected by TokenStore's OS-specific envelope.
/// Every JSON name is pinned because this is a persisted compatibility contract,
/// not an HTTP DTO that may follow the global naming policy.
/// </summary>
internal sealed record GitHubCredentialRecord
{
    public const int CurrentFormatVersion = 2;

    [JsonPropertyName("format_version")]
    public int FormatVersion { get; init; } = CurrentFormatVersion;

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

    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    [JsonIgnore]
    public bool IsRefreshable => !string.IsNullOrWhiteSpace(RefreshToken);

    public static GitHubCredentialRecord FromOAuthResponse(
        AccessTokenResponse response,
        DateTimeOffset receivedAt,
        long generation,
        GitHubCredentialRecord? previous = null)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken))
            throw new InvalidOperationException("GitHub OAuth response did not include an access token.");

        // GitHub refresh tokens rotate: after a refresh grant, the submitted
        // token is spent. Never preserve it when the response unexpectedly omits
        // its replacement; commit the new access token as non-refreshable instead.
        var refreshToken = string.IsNullOrWhiteSpace(response.RefreshToken)
            ? null
            : response.RefreshToken;
        DateTimeOffset? refreshExpiry = refreshToken is not null
            && response.RefreshTokenExpiresIn is > 0
                ? receivedAt.AddSeconds(response.RefreshTokenExpiresIn.Value)
                : null;

        return new GitHubCredentialRecord
        {
            FormatVersion = CurrentFormatVersion,
            AccessToken = response.AccessToken,
            AccessTokenExpiresAt = response.ExpiresIn is > 0
                ? receivedAt.AddSeconds(response.ExpiresIn.Value)
                : null,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshExpiry,
            TokenType = response.TokenType ?? previous?.TokenType,
            Scope = response.Scope ?? previous?.Scope,
            Generation = generation,
        };
    }

    public static GitHubCredentialRecord FromLegacyToken(string accessToken) => new()
    {
        FormatVersion = 1,
        AccessToken = accessToken,
        Generation = 0,
    };

    public override string ToString() =>
        $"GitHubCredentialRecord {{ FormatVersion = {FormatVersion}, "
        + $"AccessToken = (redacted), AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, "
        + $"RefreshToken = {(IsRefreshable ? "(redacted)" : "(none)")}, "
        + $"RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O}, "
        + $"TokenType = {TokenType}, Scope = {Scope}, Generation = {Generation} }}";
}

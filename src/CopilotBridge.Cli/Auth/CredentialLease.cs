namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Immutable credential view returned by CredentialService. It contains only what
/// AuthService needs and exposes no paths, protectors, migration records, or stores.
/// </summary>
internal sealed record CredentialLease
{
    public required int Version { get; init; }
    public required string AccessToken { get; init; }
    public DateTimeOffset? AccessTokenExpiresAt { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }
    public string? TokenType { get; init; }
    public string? Scope { get; init; }
    public required string CredentialId { get; init; }
    public required long Generation { get; init; }

    public bool IsRefreshable => !string.IsNullOrWhiteSpace(RefreshToken);
    public bool IsDirect => Version == Models.GitHub.CredentialFileRecord.GitHubCliOAuthVersion;

    public override string ToString() =>
        $"CredentialLease {{ Version = {Version}, AccessToken = (redacted), "
        + $"AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, "
        + $"RefreshToken = {(IsRefreshable ? "(redacted)" : "(none)")}, "
        + $"RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O}, Generation = {Generation} }}";
}

internal sealed record CredentialStatus(
    string Path,
    int Version,
    string? OAuthClientId,
    bool IsDirect,
    bool IsRefreshable,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? RefreshTokenExpiresAt,
    long Generation);

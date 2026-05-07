namespace CopilotBridge.Cli.Auth;

/// <summary>
/// The only public surface for authentication. Callers ask for a token; everything else
/// (device-code flow, persistence, encryption, refresh) is an implementation detail.
/// </summary>
public interface IAuthService
{
    /// <summary>True if a usable GitHub token is on disk and decryptable.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Absolute path of the encrypted token file (for UX).</summary>
    string TokenLocation { get; }

    /// <summary>
    /// CAPI base URL for the active subscription, populated after the first successful
    /// Copilot token fetch (from <c>endpoints.api</c> on the token response).
    /// Defaults to <c>https://api.githubcopilot.com</c> when the token doesn't specify.
    /// Null until <see cref="GetCopilotTokenAsync"/> has run at least once.
    /// </summary>
    string? CopilotApiBaseUrl { get; }

    /// <summary>UTC time the cached Copilot token expires; null until first fetch.</summary>
    DateTimeOffset? CopilotTokenExpiry { get; }

    /// <summary>
    /// Returns the GitHub OAuth token. If no token is cached, runs the device-code flow
    /// (notifying via the constructor callback) and persists the result.
    /// </summary>
    ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a valid Copilot bearer token. Fetches and caches on first call; subsequent
    /// calls return the cached value until expiry, after which a background timer refreshes
    /// it transparently. Throws if the GitHub token is missing — call
    /// <see cref="EnsureGitHubTokenAsync"/> first to run the device-code flow.
    /// </summary>
    ValueTask<string> GetCopilotTokenAsync(CancellationToken ct = default);

    /// <summary>Deletes the persisted token and clears in-memory caches.</summary>
    void SignOut();
}

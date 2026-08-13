namespace CopilotBridge.Cli.Auth;

/// <summary>
/// The only public surface for authentication. Callers ask for a credential/lease; everything else
/// (device-code flow, persistence, encryption, refresh) is an implementation detail.
/// </summary>
public interface IAuthService
{
    /// <summary>True if a decryptable GitHub credential is on disk.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Absolute path of the authoritative encrypted credential (for UX).</summary>
    string TokenLocation { get; }

    /// <summary>
    /// Status-only view of the current lease's CAPI base URL, populated after the first successful
    /// Copilot token fetch (from <c>endpoints.api</c> on the token response).
    /// Defaults to <c>https://api.githubcopilot.com</c> when the token doesn't specify.
    /// Null until <see cref="GetCopilotTokenAsync"/> has run at least once.
    /// </summary>
    string? CopilotApiBaseUrl { get; }

    /// <summary>UTC time the cached Copilot token expires; null until first fetch.</summary>
    DateTimeOffset? CopilotTokenExpiry { get; }

    /// <summary>
    /// Returns a usable GitHub OAuth access token. Refreshes a versioned credential
    /// before known expiry; if none exists, runs device flow and persists the complete result.
    /// </summary>
    ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns one atomic Copilot token/endpoint lease. Fetches and caches on first call;
    /// subsequent calls return the cached lease until its receipt-relative refresh deadline.
    /// Pass the lease and closed 401/403 reason that rejected it to discard exactly
    /// that generation; a newer concurrent generation is reused rather than refreshed
    /// again. Throws if the GitHub token is missing — call
    /// <see cref="EnsureGitHubTokenAsync"/> first to run the device-code flow.
    /// </summary>
    ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
        CopilotLeaseRejection? rejection = null,
        CancellationToken ct = default);

    /// <summary>Deletes every persisted credential representation and clears in-memory leases.</summary>
    void SignOut();
}

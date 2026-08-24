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
    /// lease resolution. Version 1 uses <c>endpoints.api</c> from token exchange and defaults to
    /// <c>https://api.githubcopilot.com</c>; version 2 uses that generic host directly.
    /// Null until <see cref="GetCopilotTokenAsync"/> has run at least once.
    /// </summary>
    string? CopilotApiBaseUrl { get; }

    /// <summary>
    /// UTC time the cached exchanged token expires; direct credentials report an unknown deadline.
    /// Null until first lease resolution.
    /// </summary>
    DateTimeOffset? CopilotTokenExpiry { get; }

    /// <summary>
    /// Returns a usable GitHub OAuth access token. Refreshes a versioned credential
    /// before known expiry; if none exists, runs device flow and persists the complete result.
    /// </summary>
    ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns one atomic Copilot bearer/endpoint lease. A GitHub CLI OAuth credential
    /// is used directly; a legacy Copilot Plugin credential is exchanged first. Fetches
    /// and caches on first call; subsequent calls return the cached lease until its
    /// credential-version-specific refresh deadline.
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

using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Owns the persisted GitHub OAuth credential lifecycle. It is an AuthService
/// implementation detail: callers never consume refresh tokens or storage paths.
/// </summary>
internal sealed class GitHubCredentialManager(
    IHttpClientFactory httpClientFactory,
    GitHubCredentialStore store,
    TimeProvider timeProvider,
    ILogger<GitHubCredentialManager> log) : IDisposable
{
    private static readonly TimeSpan RefreshSafetyWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RotationLockTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private long _terminalRejectedGeneration = long.MinValue;
    private bool _disposed;

    public bool IsAuthenticated => store.TryLoad() is not null;

    public string TokenLocation =>
        store.TryLoad()?.AuthoritativePath ?? store.VersionedPrimaryPath;

    public async ValueTask<GitHubCredentialRecord> GetUsableAsync(
        CancellationToken ct = default)
    {
        var current = LoadOrThrow();
        ThrowIfTerminallyRejected(current.Record);
        if (!NeedsRefresh(current.Record, timeProvider.GetUtcNow()))
            return current.Record;

        return await RefreshAsync(
            current.Record.Generation,
            force: false,
            trigger: "deadline",
            ct).ConfigureAwait(false);
    }

    public ValueTask<GitHubCredentialRecord> RefreshAfterRejectionAsync(
        long rejectedGeneration,
        CancellationToken ct = default) =>
        RefreshAsync(rejectedGeneration, force: true, trigger: "github_401", ct);

    public void MarkTerminallyRejected(long generation) =>
        Volatile.Write(ref _terminalRejectedGeneration, generation);

    public void ClearTerminalRejection() =>
        Volatile.Write(ref _terminalRejectedGeneration, long.MinValue);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshLock.Dispose();
    }

    private async ValueTask<GitHubCredentialRecord> RefreshAsync(
        long observedGeneration,
        bool force,
        string trigger,
        CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = LoadOrThrow();
            ThrowIfTerminallyRejected(current.Record);
            if (current.Record.Generation > observedGeneration)
                return current.Record;
            if (!force && !NeedsRefresh(current.Record, timeProvider.GetUtcNow()))
                return current.Record;

            EnsureRefreshable(current.Record);
            if (current.Format != GitHubCredentialFormat.Versioned)
                throw new GitHubReauthenticationRequiredException(
                    "the legacy token has no refresh metadata");

            await using var rotationLock = await store.AcquireRotationLockAsync(
                current.AuthoritativePath,
                RotationLockTimeout,
                ct).ConfigureAwait(false);

            // Another process may have consumed the rotating refresh token and
            // committed a newer generation while we waited for the file lock.
            current = LoadOrThrow();
            ThrowIfTerminallyRejected(current.Record);
            if (current.Record.Generation > observedGeneration)
                return current.Record;
            if (!force && !NeedsRefresh(current.Record, timeProvider.GetUtcNow()))
                return current.Record;
            EnsureRefreshable(current.Record);

            var started = timeProvider.GetTimestamp();
            try
            {
                var http = httpClientFactory.CreateClient(UpstreamHttpClientNames.GitHubAuth);
                var response = await GitHubAuthClient.RefreshAccessTokenAsync(
                    http, current.Record.RefreshToken!, ct).ConfigureAwait(false);
                var refreshed = GitHubCredentialRecord.FromOAuthResponse(
                    response,
                    timeProvider.GetUtcNow(),
                    checked(current.Record.Generation + 1),
                    current.Record);

                var mirrorSaved = store.SaveVersioned(refreshed, current.AuthoritativePath);
                if (!mirrorSaved)
                {
                    log.LogWarning(
                        "GitHub credential refresh outcome=success legacy_mirror=failed "
                        + "generation={Generation}",
                        refreshed.Generation);
                }
                log.LogInformation(
                    "GitHub credential refresh trigger={Trigger} outcome=success generation={Generation} "
                    + "refreshable={Refreshable} expires_in_seconds={ExpiresInSeconds} "
                    + "duration_ms={DurationMs:0}",
                    trigger,
                    refreshed.Generation,
                    refreshed.IsRefreshable,
                    RemainingSeconds(refreshed.AccessTokenExpiresAt),
                    timeProvider.GetElapsedTime(started).TotalMilliseconds);
                return refreshed;
            }
            catch (GitHubOAuthException ex)
            {
                log.LogWarning(
                    "GitHub credential refresh trigger={Trigger} outcome=reauth_required "
                    + "status={Status} error_code={ErrorCode}",
                    trigger,
                    ex.StatusCode is null ? "(none)" : ((int)ex.StatusCode).ToString(),
                    ex.ErrorCode ?? "(none)");
                throw new GitHubReauthenticationRequiredException(
                    "GitHub rejected the refresh credential", ex);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private StoredGitHubCredential LoadOrThrow() =>
        store.TryLoad() ?? throw new GitHubReauthenticationRequiredException(
            "no decryptable credential is stored");

    private void EnsureRefreshable(GitHubCredentialRecord record)
    {
        if (!record.IsRefreshable)
            throw new GitHubReauthenticationRequiredException(
                "the stored access token has no refresh token");
        if (record.RefreshTokenExpiresAt is { } expiry
            && expiry <= timeProvider.GetUtcNow())
            throw new GitHubReauthenticationRequiredException(
                "the stored refresh token has expired");
    }

    private void ThrowIfTerminallyRejected(GitHubCredentialRecord record)
    {
        if (record.Generation == Volatile.Read(ref _terminalRejectedGeneration))
            throw new GitHubReauthenticationRequiredException(
                "GitHub rejected this credential generation after bounded refresh");
    }

    private static bool NeedsRefresh(
        GitHubCredentialRecord record,
        DateTimeOffset now) =>
        record.AccessTokenExpiresAt is { } expiry
        && expiry <= now + RefreshSafetyWindow;

    private long? RemainingSeconds(DateTimeOffset? expiry) =>
        expiry is null
            ? null
            : Math.Max(0, (long)(expiry.Value - timeProvider.GetUtcNow()).TotalSeconds);
}

using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Sole owner of credential persistence, migration, OAuth login, refresh,
/// rejection identity, status, and logout.
/// </summary>
internal sealed class CredentialService : IDisposable
{
    private static readonly TimeSpan RefreshSafetyWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RotationLockTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CredentialService> _log;
    private readonly Action<DeviceCodeChallenge> _onDeviceCodeIssued;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CredentialIdentity? _terminalRejected;
    private bool _disposed;

    public CredentialService(
        IHttpClientFactory httpClientFactory,
        CredentialStore store,
        TimeProvider timeProvider,
        ILogger<CredentialService> log,
        Action<DeviceCodeChallenge>? onDeviceCodeIssued = null)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _timeProvider = timeProvider;
        _log = log;
        _onDeviceCodeIssued = onDeviceCodeIssued ?? (_ => { });
    }

    public bool IsAuthenticated => _store.LoadOrMigrate() is not null;
    public string CredentialLocation => _store.FilePath;

    public CredentialStatus? GetStatus()
    {
        var record = _store.LoadOrMigrate();
        return record is null
            ? null
            : new CredentialStatus(
                _store.FilePath,
                record.Version,
                record.IsDirect,
                record.IsRefreshable,
                record.AccessTokenExpiresAt,
                record.RefreshTokenExpiresAt,
                record.Generation);
    }

    public async ValueTask<CredentialLease> EnsureUsableAsync(
        CancellationToken ct = default)
    {
        var record = _store.LoadOrMigrate();
        if (record is not null) return await GetUsableAsync(ct).ConfigureAwait(false);

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            record = _store.LoadOrMigrate();
            return record is not null
                ? await GetUsableAsync(ct).ConfigureAwait(false)
                : await LoginCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async ValueTask<CredentialLease> LoginAsync(CancellationToken ct = default)
    {
        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoginCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async ValueTask<CredentialLease> GetUsableAsync(CancellationToken ct = default)
    {
        var record = LoadOrThrow();
        ThrowIfTerminal(record);
        if (!NeedsRefresh(record, _timeProvider.GetUtcNow())) return ToLease(record);
        return await RefreshAsync(IdentityOf(record), force: false, "deadline", ct)
            .ConfigureAwait(false);
    }

    public async ValueTask<CredentialLease> RecoverAfterRejectionAsync(
        CredentialLease rejected,
        CancellationToken ct = default)
    {
        try
        {
            return await RefreshAsync(
                IdentityOf(rejected), force: true, "github_401", ct).ConfigureAwait(false);
        }
        catch (GitHubReauthenticationRequiredException)
        {
            MarkTerminal(rejected);
            throw;
        }
    }

    public void MarkTerminal(CredentialLease credential) =>
        MarkTerminal(IdentityOf(credential));

    public void MarkTerminal(int version, string credentialId, long generation) =>
        MarkTerminal(new CredentialIdentity(version, credentialId, generation));

    public void ClearTerminalRejection() =>
        Volatile.Write(ref _terminalRejected, null);

    public void SignOut()
    {
        _store.DeleteAll();
        ClearTerminalRejection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loginLock.Dispose();
        _refreshLock.Dispose();
    }

    private async ValueTask<CredentialLease> RefreshAsync(
        CredentialIdentity observed,
        bool force,
        string trigger,
        CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = LoadOrThrow();
            ThrowIfTerminal(current);
            if (IdentityOf(current) != observed) return ToLease(current);
            if (!force && !NeedsRefresh(current, _timeProvider.GetUtcNow()))
                return ToLease(current);
            EnsureRefreshableLegacy(current);

            await using var rotationLock = await _store.AcquireLockAsync(
                RotationLockTimeout, ct).ConfigureAwait(false);
            current = LoadOrThrow();
            ThrowIfTerminal(current);
            if (IdentityOf(current) != observed) return ToLease(current);
            if (!force && !NeedsRefresh(current, _timeProvider.GetUtcNow()))
                return ToLease(current);
            EnsureRefreshableLegacy(current);

            var started = _timeProvider.GetTimestamp();
            try
            {
                var http = _httpClientFactory.CreateClient(UpstreamHttpClientNames.GitHubAuth);
                var response = await GitHubAuthClient.RefreshAccessTokenAsync(
                    http,
                    current.RefreshToken!,
                    GitHubOAuthProvider.CopilotPluginClientId,
                    ct).ConfigureAwait(false);
                var refreshed = FromRefreshResponse(current, response);
                _store.SaveWhileLocked(refreshed);
                _log.LogInformation(
                    "GitHub credential refresh trigger={Trigger} outcome=success "
                    + "credential_version={Version} generation={Generation} "
                    + "refreshable={Refreshable} expires_in_seconds={ExpiresInSeconds} "
                    + "duration_ms={DurationMs:0}",
                    trigger,
                    refreshed.Version,
                    refreshed.Generation,
                    refreshed.IsRefreshable,
                    RemainingSeconds(refreshed.AccessTokenExpiresAt),
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds);
                return ToLease(refreshed);
            }
            catch (GitHubRefreshCredentialRejectedException ex)
            {
                MarkTerminal(ToLease(current));
                _log.LogWarning(
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

    private CredentialFileRecord LoadOrThrow() =>
        _store.LoadOrMigrate() ?? throw new GitHubReauthenticationRequiredException(
            "no decryptable credential is stored");

    private async ValueTask<CredentialLease> LoginCoreAsync(CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(UpstreamHttpClientNames.GitHubAuth);
        var deviceCode = await GitHubAuthClient.RequestDeviceCodeAsync(http, ct)
            .ConfigureAwait(false);
        _onDeviceCodeIssued(new DeviceCodeChallenge(
            deviceCode.UserCode,
            deviceCode.VerificationUri,
            TimeSpan.FromSeconds(deviceCode.ExpiresIn)));
        var response = await GitHubAuthClient.PollAccessTokenAsync(http, deviceCode, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.AccessToken))
            throw new GitHubOAuthException(
                "device-token exchange", "missing_access_token");

        var receivedAt = _timeProvider.GetUtcNow();
        var record = new CredentialFileRecord
        {
            Version = CredentialFileRecord.GitHubCliOAuthVersion,
            AccessToken = response.AccessToken,
            AccessTokenExpiresAt = response.ExpiresIn is > 0
                ? receivedAt.AddSeconds(response.ExpiresIn.Value)
                : null,
            RefreshToken = string.IsNullOrWhiteSpace(response.RefreshToken)
                ? null
                : response.RefreshToken,
            RefreshTokenExpiresAt = response.RefreshToken is { Length: > 0 }
                && response.RefreshTokenExpiresIn is > 0
                    ? receivedAt.AddSeconds(response.RefreshTokenExpiresIn.Value)
                    : null,
            TokenType = response.TokenType,
            Scope = response.Scope,
            CredentialId = Guid.NewGuid().ToString("N"),
            Generation = 1,
        };
        _store.Save(record);
        ClearTerminalRejection();
        _log.LogInformation(
            "GitHub device login outcome=success credential_version={Version} "
            + "refreshable={Refreshable} expires_in_seconds={ExpiresInSeconds} "
            + "refresh_expires_in_seconds={RefreshExpiresInSeconds}",
            record.Version,
            record.IsRefreshable,
            RemainingSeconds(record.AccessTokenExpiresAt),
            RemainingSeconds(record.RefreshTokenExpiresAt));
        return ToLease(record);
    }

    private void ThrowIfTerminal(CredentialFileRecord record)
    {
        if (IdentityOf(record) == Volatile.Read(ref _terminalRejected))
            throw new GitHubReauthenticationRequiredException(
                "GitHub rejected this credential generation after bounded recovery");
    }

    private void MarkTerminal(CredentialIdentity rejected)
    {
        var current = _store.TryLoad();
        if (current is not null && IdentityOf(current) == rejected)
            Volatile.Write(ref _terminalRejected, rejected);
    }

    private void EnsureRefreshableLegacy(CredentialFileRecord record)
    {
        if (record.Version != CredentialFileRecord.CopilotPluginVersion)
            throw new GitHubReauthenticationRequiredException(
                "the direct credential was rejected and requires interactive login");
        if (!record.IsRefreshable)
            throw new GitHubReauthenticationRequiredException(
                "the stored legacy access token has no refresh token");
        if (record.RefreshTokenExpiresAt is { } expiry
            && expiry <= _timeProvider.GetUtcNow())
            throw new GitHubReauthenticationRequiredException(
                "the stored refresh token has expired");
    }

    private CredentialFileRecord FromRefreshResponse(
        CredentialFileRecord current,
        AccessTokenResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken))
            throw new InvalidOperationException("GitHub OAuth refresh returned no access token.");
        var now = _timeProvider.GetUtcNow();
        var refreshToken = string.IsNullOrWhiteSpace(response.RefreshToken)
            ? null
            : response.RefreshToken;
        return new CredentialFileRecord
        {
            Version = CredentialFileRecord.CopilotPluginVersion,
            AccessToken = response.AccessToken,
            AccessTokenExpiresAt = response.ExpiresIn is > 0
                ? now.AddSeconds(response.ExpiresIn.Value)
                : null,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshToken is not null
                && response.RefreshTokenExpiresIn is > 0
                    ? now.AddSeconds(response.RefreshTokenExpiresIn.Value)
                    : null,
            TokenType = response.TokenType ?? current.TokenType,
            Scope = response.Scope ?? current.Scope,
            CredentialId = current.CredentialId,
            Generation = checked(current.Generation + 1),
        };
    }

    private static bool NeedsRefresh(CredentialFileRecord record, DateTimeOffset now) =>
        record.AccessTokenExpiresAt is { } expiry
        && expiry <= now + RefreshSafetyWindow;

    private long? RemainingSeconds(DateTimeOffset? expiry) =>
        expiry is null ? null : Math.Max(0, (long)(expiry.Value - _timeProvider.GetUtcNow()).TotalSeconds);

    private static CredentialLease ToLease(CredentialFileRecord record) => new()
    {
        Version = record.Version,
        AccessToken = record.AccessToken,
        AccessTokenExpiresAt = record.AccessTokenExpiresAt,
        RefreshToken = record.RefreshToken,
        RefreshTokenExpiresAt = record.RefreshTokenExpiresAt,
        TokenType = record.TokenType,
        Scope = record.Scope,
        CredentialId = record.CredentialId,
        Generation = record.Generation,
    };

    private static CredentialIdentity IdentityOf(CredentialFileRecord record) =>
        new(record.Version, record.CredentialId, record.Generation);

    private static CredentialIdentity IdentityOf(CredentialLease record) =>
        new(record.Version, record.CredentialId, record.Generation);

    private sealed record CredentialIdentity(int Version, string CredentialId, long Generation);
}

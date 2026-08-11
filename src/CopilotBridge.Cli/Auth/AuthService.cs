using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Sealed authentication facade. Owns device login, persisted GitHub OAuth
/// refresh state, and in-memory Copilot bearer leases. Callers never read token
/// files or invoke OAuth helpers directly.
/// </summary>
public sealed class AuthService : IAuthService, IDisposable
{
    private const string DefaultCopilotApiBaseUrl = "https://api.githubcopilot.com";
    private static readonly TimeSpan CopilotSafetyWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CopilotReceiptBuffer = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinRefreshDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubCredentialStore _credentialStore;
    private readonly GitHubCredentialManager _githubCredentials;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthService> _log;
    private readonly Action<DeviceCodeChallenge> _onDeviceCodeIssued;
    private readonly bool _enableBackgroundRefresh;
    private readonly SemaphoreSlim _githubLoginLock = new(1, 1);
    private readonly SemaphoreSlim _copilotFetchLock = new(1, 1);

    private CopilotAuthLease? _copilotCache;
    private ITimer? _refreshTimer;
    private long _copilotGeneration;
    private bool _disposed;

    public AuthService(
        IHttpClientFactory httpClientFactory,
        Action<DeviceCodeChallenge>? onDeviceCodeIssued = null)
        : this(
            httpClientFactory,
            TokenStore.CredentialStore,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            onDeviceCodeIssued,
            enableBackgroundRefresh: true)
    {
    }

    internal AuthService(
        IHttpClientFactory httpClientFactory,
        GitHubCredentialStore credentialStore,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        Action<DeviceCodeChallenge>? onDeviceCodeIssued,
        bool enableBackgroundRefresh)
    {
        _httpClientFactory = httpClientFactory;
        _credentialStore = credentialStore;
        _timeProvider = timeProvider;
        _log = loggerFactory.CreateLogger<AuthService>();
        _onDeviceCodeIssued = onDeviceCodeIssued ?? (_ => { });
        _enableBackgroundRefresh = enableBackgroundRefresh;
        _githubCredentials = new GitHubCredentialManager(
            httpClientFactory,
            credentialStore,
            timeProvider,
            loggerFactory.CreateLogger<GitHubCredentialManager>());
    }

    public bool IsAuthenticated => _githubCredentials.IsAuthenticated;

    public string TokenLocation => _githubCredentials.TokenLocation;

    public string? CopilotApiBaseUrl
    {
        get
        {
#if DEBUG
            var testBaseUrl = Environment.GetEnvironmentVariable(
                "COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL");
            if (!string.IsNullOrWhiteSpace(testBaseUrl)) return testBaseUrl;
#endif
            return Volatile.Read(ref _copilotCache)?.ApiBaseUrl;
        }
    }

    public DateTimeOffset? CopilotTokenExpiry =>
        Volatile.Read(ref _copilotCache)?.ServerExpiresAt;

    public async ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default)
    {
        if (_credentialStore.TryLoad() is not null)
            return (await _githubCredentials.GetUsableAsync(ct).ConfigureAwait(false)).AccessToken;

        await _githubLoginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credentialStore.TryLoad() is not null)
                return (await _githubCredentials.GetUsableAsync(ct).ConfigureAwait(false)).AccessToken;

            var http = _httpClientFactory.CreateClient(UpstreamHttpClientNames.GitHubAuth);
            var deviceCode = await GitHubAuthClient.RequestDeviceCodeAsync(http, ct)
                .ConfigureAwait(false);
            _onDeviceCodeIssued(new DeviceCodeChallenge(
                deviceCode.UserCode,
                deviceCode.VerificationUri,
                TimeSpan.FromSeconds(deviceCode.ExpiresIn)));

            var response = await GitHubAuthClient.PollAccessTokenAsync(http, deviceCode, ct)
                .ConfigureAwait(false);
            var credential = GitHubCredentialRecord.FromOAuthResponse(
                response,
                _timeProvider.GetUtcNow(),
                generation: 1);
            var mirrorSaved = _credentialStore.SaveNew(credential);
            _githubCredentials.ClearTerminalRejection();
            if (!mirrorSaved)
            {
                _log.LogWarning(
                    "GitHub device login outcome=success legacy_mirror=failed generation={Generation}",
                    credential.Generation);
            }
            _log.LogInformation(
                "GitHub device login outcome=success credential_format=v2 refreshable={Refreshable} "
                + "expires_in_seconds={ExpiresInSeconds} "
                + "refresh_expires_in_seconds={RefreshExpiresInSeconds}",
                credential.IsRefreshable,
                RemainingSeconds(credential.AccessTokenExpiresAt),
                RemainingSeconds(credential.RefreshTokenExpiresAt));
            return credential.AccessToken;
        }
        finally
        {
            _githubLoginLock.Release();
        }
    }

    public async ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
        CopilotAuthLease? rejectedLease = null,
        CancellationToken ct = default)
    {
#if DEBUG
        var testBaseUrl = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL");
        if (!string.IsNullOrWhiteSpace(testBaseUrl))
        {
            var existingTestLease = Volatile.Read(ref _copilotCache);
            if (existingTestLease is not null) return existingTestLease;
            var testLease = new CopilotAuthLease
            {
                Token = "behavior-test-token",
                ApiBaseUrl = testBaseUrl,
                RefreshAt = DateTimeOffset.MaxValue,
                ServerExpiresAt = DateTimeOffset.MaxValue,
                Generation = 1,
            };
            Volatile.Write(ref _copilotCache, testLease);
            return testLease;
        }
#endif

        var snapshot = Volatile.Read(ref _copilotCache);
        if (CanReuse(snapshot, rejectedLease)) return snapshot!;

        await _copilotFetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = Volatile.Read(ref _copilotCache);
            if (CanReuse(snapshot, rejectedLease)) return snapshot!;

            var rejectingCurrent = rejectedLease is not null
                && snapshot is not null
                && snapshot.Generation == rejectedLease.Generation;
            if (rejectingCurrent)
            {
                Volatile.Write(ref _copilotCache, null);
                StopRefreshTimer();
            }

            return await FetchAndCacheAsync(
                rejectingCurrent ? "copilot_401" : "deadline",
                ct).ConfigureAwait(false);
        }
        finally
        {
            _copilotFetchLock.Release();
        }
    }

    internal async ValueTask<GitHubUser> GetGitHubUserAsync(
        CancellationToken ct = default)
    {
        var credential = await _githubCredentials.GetUsableAsync(ct).ConfigureAwait(false);
        using var first = await SendGitHubUserRequestAsync(credential.AccessToken, ct)
            .ConfigureAwait(false);
        if (first.StatusCode != HttpStatusCode.Unauthorized)
            return await ReadGitHubUserAsync(first, ct).ConfigureAwait(false);

        GitHubCredentialRecord refreshed;
        try
        {
            refreshed = await _githubCredentials.RefreshAfterRejectionAsync(
                credential.Generation, ct).ConfigureAwait(false);
        }
        catch (GitHubReauthenticationRequiredException)
        {
            _githubCredentials.MarkTerminallyRejected(credential.Generation);
            throw;
        }
        using var second = await SendGitHubUserRequestAsync(refreshed.AccessToken, ct)
            .ConfigureAwait(false);
        if (second.StatusCode == HttpStatusCode.Unauthorized)
        {
            _githubCredentials.MarkTerminallyRejected(refreshed.Generation);
            throw new GitHubReauthenticationRequiredException(
                "the refreshed GitHub access token was also rejected by user lookup",
                new GitHubApiRequestException("user lookup", second.StatusCode));
        }
        return await ReadGitHubUserAsync(second, ct).ConfigureAwait(false);
    }

    public void SignOut()
    {
        StopRefreshTimer();
        Volatile.Write(ref _copilotCache, null);
        _credentialStore.Delete();
        _githubCredentials.ClearTerminalRejection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRefreshTimer();
        _githubCredentials.Dispose();
        _githubLoginLock.Dispose();
        _copilotFetchLock.Dispose();
    }

    private bool CanReuse(
        CopilotAuthLease? snapshot,
        CopilotAuthLease? rejectedLease)
    {
        if (snapshot is null || snapshot.RefreshAt <= _timeProvider.GetUtcNow())
            return false;
        return rejectedLease is null || snapshot.Generation != rejectedLease.Generation;
    }

    private async Task<CopilotAuthLease> FetchAndCacheAsync(
        string trigger,
        CancellationToken ct)
    {
        var credential = await _githubCredentials.GetUsableAsync(ct).ConfigureAwait(false);
        CopilotTokenResponse response;
        try
        {
            response = await FetchCopilotTokenAsync(credential.AccessToken, ct)
                .ConfigureAwait(false);
        }
        catch (GitHubApiRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            try
            {
                credential = await _githubCredentials.RefreshAfterRejectionAsync(
                    credential.Generation, ct).ConfigureAwait(false);
            }
            catch (GitHubReauthenticationRequiredException)
            {
                _githubCredentials.MarkTerminallyRejected(credential.Generation);
                throw;
            }

            try
            {
                response = await FetchCopilotTokenAsync(credential.AccessToken, ct)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiRequestException retryEx) when (
                retryEx.StatusCode == HttpStatusCode.Unauthorized)
            {
                _githubCredentials.MarkTerminallyRejected(credential.Generation);
                throw new GitHubReauthenticationRequiredException(
                    "the refreshed GitHub access token was also rejected", retryEx);
            }
        }

        var receivedAt = _timeProvider.GetUtcNow();
        var effectiveExpiry = receivedAt.AddSeconds(Math.Max(1, response.RefreshIn))
            + CopilotReceiptBuffer;
        var refreshAt = effectiveExpiry - CopilotSafetyWindow;
        if (refreshAt <= receivedAt)
            refreshAt = receivedAt + MinRefreshDelay;

        DateTimeOffset serverExpiry;
        try { serverExpiry = DateTimeOffset.FromUnixTimeSeconds(response.ExpiresAt); }
        catch (ArgumentOutOfRangeException) { serverExpiry = effectiveExpiry; }

        var lease = new CopilotAuthLease
        {
            Token = response.Token,
            ApiBaseUrl = response.Endpoints?.Api ?? DefaultCopilotApiBaseUrl,
            RefreshAt = refreshAt,
            ServerExpiresAt = serverExpiry,
            Generation = Interlocked.Increment(ref _copilotGeneration),
        };
        Volatile.Write(ref _copilotCache, lease);
        ScheduleRefresh(refreshAt - receivedAt);
        _log.LogInformation(
            "Copilot bearer refresh trigger={Trigger} outcome=success generation={Generation} "
            + "api_host={ApiHost} refresh_in_seconds={RefreshInSeconds}",
            trigger,
            lease.Generation,
            SafeHost(lease.ApiBaseUrl),
            Math.Max(0, (long)(refreshAt - receivedAt).TotalSeconds));
        return lease;
    }

    private async ValueTask<CopilotTokenResponse> FetchCopilotTokenAsync(
        string githubToken,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(UpstreamHttpClientNames.GitHubAuth);
        return await CopilotTokenClient.FetchAsync(http, githubToken, ct)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGitHubUserRequestAsync(
        string accessToken,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(UpstreamHttpClientNames.GitHubAuth);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
    }

    private static async ValueTask<GitHubUser> ReadGitHubUserAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiRequestException("user lookup", response.StatusCode);
        return await response.Content.ReadFromJsonAsync(JsonContext.Default.GitHubUser, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub user lookup returned an empty response.");
    }

    private void ScheduleRefresh(TimeSpan delay)
    {
        if (_disposed || !_enableBackgroundRefresh) return;
        StopRefreshTimer();
        _refreshTimer = _timeProvider.CreateTimer(
            _ => _ = RefreshTimerTickAsync(),
            state: null,
            delay < MinRefreshDelay ? MinRefreshDelay : delay,
            Timeout.InfiniteTimeSpan);
    }

    private void StopRefreshTimer()
    {
        var timer = Interlocked.Exchange(ref _refreshTimer, null);
        timer?.Dispose();
    }

    private async Task RefreshTimerTickAsync()
    {
        if (_disposed) return;
        try
        {
            await _copilotFetchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                await FetchAndCacheAsync("timer", CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _copilotFetchLock.Release();
            }
        }
        catch (GitHubReauthenticationRequiredException ex)
        {
            _log.LogError(
                "Copilot bearer refresh trigger=timer outcome=terminal_reauth_required type={Type}",
                ex.GetType().Name);
            StopRefreshTimer();
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Copilot bearer refresh trigger=timer outcome=retry type={Type}",
                ex.GetType().Name);
            if (!_disposed) ScheduleRefresh(RefreshFailureBackoff);
        }
    }

    private long? RemainingSeconds(DateTimeOffset? expiry) =>
        expiry is null
            ? null
            : Math.Max(0, (long)(expiry.Value - _timeProvider.GetUtcNow()).TotalSeconds);

    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid)";
}

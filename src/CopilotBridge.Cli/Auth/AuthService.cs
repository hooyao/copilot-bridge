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
/// Authentication facade for Copilot leases. Credential files, migration, OAuth,
/// refresh, and rejection state are exclusively owned by CredentialService.
/// </summary>
public sealed class AuthService : IAuthService, IDisposable
{
    private const string DefaultCopilotApiBaseUrl = "https://api.githubcopilot.com";
    private static readonly TimeSpan CopilotSafetyWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CopilotReceiptBuffer = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinRefreshDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialService _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthService> _log;
    private readonly bool _enableBackgroundRefresh;
    private readonly bool _ownsCredentialService;
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
            CreateDefaultCredentialService(httpClientFactory, onDeviceCodeIssued),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: true,
            ownsCredentialService: true)
    {
    }

    internal AuthService(
        IHttpClientFactory httpClientFactory,
        CredentialService credentials,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        bool enableBackgroundRefresh,
        bool ownsCredentialService = false)
    {
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _timeProvider = timeProvider;
        _log = loggerFactory.CreateLogger<AuthService>();
        _enableBackgroundRefresh = enableBackgroundRefresh;
        _ownsCredentialService = ownsCredentialService;
    }

    public bool IsAuthenticated
    {
        get
        {
#if DEBUG
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    "COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL")))
                return true;
#endif
            return _credentials.IsAuthenticated;
        }
    }

    public string TokenLocation => _credentials.CredentialLocation;

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

    internal CredentialStatus? GetCredentialStatus() => _credentials.GetStatus();

    internal async ValueTask<CredentialLease> LoginAsync(CancellationToken ct = default)
    {
        StopRefreshTimer();
        Volatile.Write(ref _copilotCache, null);
        return await _credentials.LoginAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default)
    {
#if DEBUG
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                "COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL")))
            return "behavior-test-github-token";
#endif
        return (await _credentials.EnsureUsableAsync(ct).ConfigureAwait(false)).AccessToken;
    }

    public async ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
        CopilotLeaseRejection? rejection = null,
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

        var rejectsDirectCredential = RejectsDirectCredential(rejection);
        if (rejectsDirectCredential)
        {
            var rejected = rejection!.Value.Lease;
            _credentials.MarkTerminal(
                rejected.CredentialVersion,
                rejected.CredentialId,
                rejected.CredentialGeneration);
            InvalidateCachedCredential(rejected);
        }

        var snapshot = ReadCacheWithoutTerminalDirectCredential();
        if (CanReuse(snapshot, rejection)) return snapshot!;

        await _copilotFetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = ReadCacheWithoutTerminalDirectCredential();
            if (CanReuse(snapshot, rejection)) return snapshot!;

            var rejectingCurrent = rejection is not null
                && snapshot is not null
                && (rejectsDirectCredential
                    ? SameCredential(snapshot, rejection.Value.Lease)
                    : snapshot.Generation == rejection.Value.Lease.Generation);
            if (rejectingCurrent)
            {
                Volatile.Write(ref _copilotCache, null);
                StopRefreshTimer();
            }

            return await FetchAndCacheAsync(
                rejectingCurrent || rejectsDirectCredential
                    ? TriggerFor(rejection!.Value.Reason)
                    : "deadline",
                ct).ConfigureAwait(false);
        }
        finally
        {
            _copilotFetchLock.Release();
        }
    }

    internal async ValueTask<GitHubUser> GetGitHubUserAsync(CancellationToken ct = default)
    {
        var credential = await _credentials.GetUsableAsync(ct).ConfigureAwait(false);
        using var first = await SendGitHubUserRequestAsync(credential.AccessToken, ct)
            .ConfigureAwait(false);
        if (first.StatusCode != HttpStatusCode.Unauthorized)
            return await ReadGitHubUserAsync(first, ct).ConfigureAwait(false);

        var recovered = await _credentials.RecoverAfterRejectionAsync(credential, ct)
            .ConfigureAwait(false);
        using var second = await SendGitHubUserRequestAsync(recovered.AccessToken, ct)
            .ConfigureAwait(false);
        if (second.StatusCode == HttpStatusCode.Unauthorized)
        {
            _credentials.MarkTerminal(recovered);
            throw new GitHubReauthenticationRequiredException(
                "the recovered GitHub credential was also rejected by user lookup",
                new GitHubApiRequestException("user lookup", second.StatusCode));
        }
        return await ReadGitHubUserAsync(second, ct).ConfigureAwait(false);
    }

    public void SignOut()
    {
        StopRefreshTimer();
        Volatile.Write(ref _copilotCache, null);
        _credentials.SignOut();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRefreshTimer();
        if (_ownsCredentialService) _credentials.Dispose();
        _copilotFetchLock.Dispose();
    }

    private bool CanReuse(CopilotAuthLease? snapshot, CopilotLeaseRejection? rejection)
    {
        if (snapshot is null || snapshot.RefreshAt <= _timeProvider.GetUtcNow()) return false;
        if (IsTerminalDirectCredential(snapshot)) return false;
        if (rejection is null) return true;
        return RejectsDirectCredential(rejection)
            ? !SameCredential(snapshot, rejection.Value.Lease)
            : snapshot.Generation != rejection.Value.Lease.Generation;
    }

    private CopilotAuthLease? ReadCacheWithoutTerminalDirectCredential()
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _copilotCache);
            if (snapshot is null || !IsTerminalDirectCredential(snapshot)) return snapshot;
            InvalidateCachedCredential(snapshot);
        }
    }

    private bool IsTerminalDirectCredential(CopilotAuthLease lease) =>
        lease.Kind == CopilotLeaseKind.Direct
        && _credentials.IsTerminal(
            lease.CredentialVersion,
            lease.CredentialId,
            lease.CredentialGeneration);

    private void InvalidateCachedCredential(CopilotAuthLease credential)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _copilotCache);
            if (snapshot is null || !SameCredential(snapshot, credential)) return;
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _copilotCache, null, snapshot),
                    snapshot))
            {
                return;
            }
        }
    }

    private static bool RejectsDirectCredential(CopilotLeaseRejection? rejection) =>
        rejection is { } value
        && value.Reason == CopilotLeaseRejectionReason.Unauthorized
        && value.Lease.Kind == CopilotLeaseKind.Direct;

    private static bool SameCredential(CopilotAuthLease left, CopilotAuthLease right) =>
        left.CredentialVersion == right.CredentialVersion
        && string.Equals(left.CredentialId, right.CredentialId, StringComparison.Ordinal)
        && left.CredentialGeneration == right.CredentialGeneration;

    private static string TriggerFor(CopilotLeaseRejectionReason reason) => reason switch
    {
        CopilotLeaseRejectionReason.Unauthorized => "copilot_401",
        CopilotLeaseRejectionReason.Forbidden => "copilot_403",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private async Task<CopilotAuthLease> FetchAndCacheAsync(string trigger, CancellationToken ct)
    {
        var credential = await _credentials.GetUsableAsync(ct).ConfigureAwait(false);
        if (credential.IsDirect) return PublishDirectLease(credential, trigger);

        CopilotTokenResponse response;
        try
        {
            response = await FetchCopilotTokenAsync(credential.AccessToken, ct)
                .ConfigureAwait(false);
        }
        catch (GitHubApiRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            credential = await _credentials.RecoverAfterRejectionAsync(credential, ct)
                .ConfigureAwait(false);
            try
            {
                response = await FetchCopilotTokenAsync(credential.AccessToken, ct)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiRequestException retryEx) when (
                retryEx.StatusCode == HttpStatusCode.Unauthorized)
            {
                _credentials.MarkTerminal(credential);
                throw new GitHubReauthenticationRequiredException(
                    "the recovered GitHub credential was also rejected", retryEx);
            }
        }

        var receivedAt = _timeProvider.GetUtcNow();
        var effectiveExpiry = receivedAt.AddSeconds(Math.Max(1, response.RefreshIn))
            + CopilotReceiptBuffer;
        var refreshAt = effectiveExpiry - CopilotSafetyWindow;
        if (refreshAt <= receivedAt) refreshAt = receivedAt + MinRefreshDelay;

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
            CredentialVersion = credential.Version,
            CredentialId = credential.CredentialId,
            CredentialGeneration = credential.Generation,
        };
        Volatile.Write(ref _copilotCache, lease);
        ScheduleRefresh(refreshAt - receivedAt);
        _log.LogInformation(
            "Copilot bearer refresh trigger={Trigger} outcome=success generation={Generation} "
            + "credential_version={CredentialVersion} api_host={ApiHost} "
            + "refresh_in_seconds={RefreshInSeconds}",
            trigger,
            lease.Generation,
            credential.Version,
            SafeHost(lease.ApiBaseUrl),
            Math.Max(0, (long)(refreshAt - receivedAt).TotalSeconds));
        return lease;
    }

    private CopilotAuthLease PublishDirectLease(CredentialLease credential, string trigger)
    {
        StopRefreshTimer();
        var expiry = credential.AccessTokenExpiresAt ?? DateTimeOffset.MaxValue;
        var lease = new CopilotAuthLease
        {
            Token = credential.AccessToken,
            ApiBaseUrl = DefaultCopilotApiBaseUrl,
            RefreshAt = expiry,
            ServerExpiresAt = expiry,
            Generation = Interlocked.Increment(ref _copilotGeneration),
            Kind = CopilotLeaseKind.Direct,
            CredentialVersion = credential.Version,
            CredentialId = credential.CredentialId,
            CredentialGeneration = credential.Generation,
        };
        Volatile.Write(ref _copilotCache, lease);
        _log.LogInformation(
            "Copilot direct lease trigger={Trigger} outcome=success credential_version={CredentialVersion} "
            + "generation={Generation} api_host={ApiHost} expiry_known={ExpiryKnown}",
            trigger,
            credential.Version,
            lease.Generation,
            SafeHost(lease.ApiBaseUrl),
            credential.AccessTokenExpiresAt is not null);
        return lease;
    }

    private async ValueTask<CopilotTokenResponse> FetchCopilotTokenAsync(
        string githubToken,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(UpstreamHttpClientNames.GitHubAuth);
        return await CopilotTokenClient.FetchAsync(http, githubToken, ct).ConfigureAwait(false);
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
            finally { _copilotFetchLock.Release(); }
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

    private static CredentialService CreateDefaultCredentialService(
        IHttpClientFactory factory,
        Action<DeviceCodeChallenge>? onDeviceCodeIssued) => new(
            factory,
            TokenStore.CreateUnifiedCredentialStore(),
            TimeProvider.System,
            NullLogger<CredentialService>.Instance,
            onDeviceCodeIssued);

    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid)";
}

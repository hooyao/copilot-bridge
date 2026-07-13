namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Self-contained authentication service. Owns:
/// <list type="bullet">
///   <item>GitHub device-code flow + GitHub OAuth token</item>
///   <item>DPAPI-encrypted token persistence next to the .exe</item>
///   <item>Copilot bearer token (in-memory, auto-refreshed on a background timer)</item>
/// </list>
/// Callers only need <see cref="IAuthService.GetCopilotTokenAsync"/>.
/// </summary>
public sealed class AuthService : IAuthService, IDisposable
{
    private const string DefaultCopilotApiBaseUrl = "https://api.githubcopilot.com";
    private static readonly TimeSpan MinRefreshDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FreshnessSlack = TimeSpan.FromSeconds(60);

    private readonly GitHubAuthClient _gitHubClient;
    private readonly CopilotTokenClient _copilotTokenClient;
    private readonly Action<DeviceCodeChallenge> _onDeviceCodeIssued;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private CopilotTokenSnapshot? _copilotCache;
    private Timer? _refreshTimer;
    private bool _disposed;

    public AuthService(HttpClient http, Action<DeviceCodeChallenge>? onDeviceCodeIssued = null)
    {
        _gitHubClient = new GitHubAuthClient(http);
        _copilotTokenClient = new CopilotTokenClient(http);
        _onDeviceCodeIssued = onDeviceCodeIssued ?? (_ => { });
    }

    public bool IsAuthenticated => TokenStore.TryLoad() is not null;

    public string TokenLocation => TokenStore.FilePath;

    public string? CopilotApiBaseUrl
    {
        get
        {
#if DEBUG
            var testBaseUrl = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL");
            if (!string.IsNullOrWhiteSpace(testBaseUrl)) return testBaseUrl;
#endif
            return Volatile.Read(ref _copilotCache)?.ApiBaseUrl;
        }
    }

    public DateTimeOffset? CopilotTokenExpiry => Volatile.Read(ref _copilotCache)?.Expiry;

    public async ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default)
    {
        var existing = TokenStore.TryLoad();
        if (existing is not null) return existing;

        var deviceCode = await _gitHubClient.RequestDeviceCodeAsync(ct);
        _onDeviceCodeIssued(new DeviceCodeChallenge(
            deviceCode.UserCode,
            deviceCode.VerificationUri,
            TimeSpan.FromSeconds(deviceCode.ExpiresIn)));

        var token = await _gitHubClient.PollAccessTokenAsync(deviceCode, ct);
        TokenStore.Save(token);
        return token;
    }

    public async ValueTask<string> GetCopilotTokenAsync(CancellationToken ct = default)
    {
#if DEBUG
        // Real-client behavior tests need a deterministic upstream that can stall
        // after headers. This override is deliberately absent from Release/AOT
        // builds; setting the variable on a shipped binary has no effect.
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL")))
            return "behavior-test-token";
#endif
        var snapshot = Volatile.Read(ref _copilotCache);
        if (IsFresh(snapshot)) return snapshot!.Token;

        await _fetchLock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock — a concurrent caller may have just refreshed.
            snapshot = Volatile.Read(ref _copilotCache);
            if (IsFresh(snapshot)) return snapshot!.Token;

            var githubToken = TokenStore.TryLoad()
                ?? throw new InvalidOperationException(
                    "Not logged in to GitHub. Run the device-code flow first (e.g. `auth login`).");

            snapshot = await FetchAndCacheAsync(githubToken, ct);
            return snapshot.Token;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public void SignOut()
    {
        StopRefreshTimer();
        Volatile.Write(ref _copilotCache, null);
        TokenStore.Delete();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRefreshTimer();
        _fetchLock.Dispose();
    }

    private static bool IsFresh(CopilotTokenSnapshot? snapshot) =>
        snapshot is not null && snapshot.Expiry > DateTimeOffset.UtcNow + FreshnessSlack;

    private async Task<CopilotTokenSnapshot> FetchAndCacheAsync(string githubToken, CancellationToken ct)
    {
        var response = await _copilotTokenClient.FetchAsync(githubToken, ct);

        var snapshot = new CopilotTokenSnapshot(
            Token: response.Token,
            Expiry: DateTimeOffset.FromUnixTimeSeconds(response.ExpiresAt),
            ApiBaseUrl: response.Endpoints?.Api ?? DefaultCopilotApiBaseUrl);

        Volatile.Write(ref _copilotCache, snapshot);

        var refreshDelay = TimeSpan.FromSeconds(Math.Max(MinRefreshDelay.TotalSeconds, response.RefreshIn - 60));
        ScheduleRefresh(refreshDelay);

        return snapshot;
    }

    private void ScheduleRefresh(TimeSpan delay)
    {
        if (_disposed) return;
        StopRefreshTimer();
        _refreshTimer = new Timer(_ => _ = RefreshTimerTickAsync(), state: null, delay, Timeout.InfiniteTimeSpan);
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
            var githubToken = TokenStore.TryLoad();
            if (githubToken is null)
            {
                // Signed out between schedule and tick — stop the loop.
                Volatile.Write(ref _copilotCache, null);
                return;
            }

            await _fetchLock.WaitAsync();
            try
            {
                if (_disposed) return;
                await FetchAndCacheAsync(githubToken, CancellationToken.None);
            }
            finally
            {
                _fetchLock.Release();
            }
        }
        catch
        {
            // Refresh failed (network blip, GitHub 5xx, etc.). Re-arm a short retry; if the
            // current cached token is still within its hard expiry, callers keep using it.
            if (!_disposed) ScheduleRefresh(RefreshFailureBackoff);
        }
    }

    private sealed record CopilotTokenSnapshot(string Token, DateTimeOffset Expiry, string ApiBaseUrl);
}

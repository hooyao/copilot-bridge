using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: fresh bridge authentication uses the official GitHub Copilot Plugin
/// device flow and persists the issuing OAuth client id in credential version 3.
/// Existing version-2 GitHub CLI direct credentials remain compatible.
/// </summary>
public sealed class GitHubOAuthContractTests : IDisposable
{
    private const string CopilotPluginClientId = "Iv1.b507a08c87ecfe98";
    private const string CopilotPluginScopes = "read:user";
    private const string PluginToken = "ghu_PLUGIN_CREDENTIAL_DO_NOT_LOG";
    private const string PluginRefreshToken = "ghr_PLUGIN_REFRESH_DO_NOT_LOG";
    private const string DirectToken = "gho_DIRECT_CREDENTIAL_DO_NOT_LOG";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-github-oauth-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _time = new(
        new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Device_code_request_matches_Copilot_Plugin_OAuth_contract()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {
                  "device_code":"device-secret",
                  "user_code":"ABCD-EFGH",
                  "verification_uri":"https://github.com/login/device",
                  "expires_in":900,
                  "interval":5
                }
                """),
        ]));
        using var http = new HttpClient(handler);

        var code = await GitHubAuthClient.RequestDeviceCodeAsync(http, CancellationToken.None);

        Assert.Equal("ABCD-EFGH", code.UserCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://github.com/login/device/code", request.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        var fields = ParseForm(request.Body);
        Assert.Equal(CopilotPluginClientId, fields["client_id"]);
        Assert.Equal(CopilotPluginScopes, fields["scope"]);
        Assert.DoesNotContain("client_secret", fields.Keys);
    }

    [Fact]
    public async Task Device_token_poll_matches_Copilot_Plugin_OAuth_contract()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"" + PluginToken
                + "\",\"token_type\":\"bearer\",\"scope\":\"read:user\"}"),
        ]));
        using var http = new HttpClient(handler);
        var code = new DeviceCodeResponse(
            "device-secret",
            "ABCD-EFGH",
            "https://github.com/login/device",
            ExpiresIn: 900,
            Interval: -1);

        var token = await GitHubAuthClient.PollAccessTokenAsync(
            http, code, CancellationToken.None);

        Assert.Equal(PluginToken, token.AccessToken);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://github.com/login/oauth/access_token", request.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        var fields = ParseForm(request.Body);
        Assert.Equal(CopilotPluginClientId, fields["client_id"]);
        Assert.Equal("device-secret", fields["device_code"]);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", fields["grant_type"]);
        Assert.DoesNotContain("client_secret", fields.Keys);
    }

    [Fact]
    public async Task Fresh_login_commits_version_three_with_explicit_Copilot_Plugin_client_id()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {
                  "device_code":"device-secret",
                  "user_code":"ABCD-EFGH",
                  "verification_uri":"https://github.com/login/device",
                  "expires_in":900,
                  "interval":-1
                }
                """),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"" + PluginToken
                + "\",\"expires_in\":28800,\"refresh_token\":\"" + PluginRefreshToken
                + "\",\"refresh_token_expires_in\":15811200,"
                + "\"token_type\":\"bearer\",\"scope\":\"read:user\"}"),
        ]));
        var directory = Path.Combine(_root, "fresh-login");
        var protector = new TestProtector();
        var store = new CredentialStore(directory, protector);
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance, _ => { });
        using var auth = new AuthService(
            factory,
            credentials,
            _time,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: false,
            ownsCredentialService: true);

        var token = await auth.EnsureGitHubTokenAsync(CancellationToken.None);

        Assert.Equal(PluginToken, token);
        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));
        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion, loaded.Version);
        Assert.Equal(CopilotPluginClientId, loaded.OAuthClientId);
        Assert.Equal(PluginRefreshToken, loaded.RefreshToken);
        Assert.True(loaded.IsRefreshable);
    }

    [Fact]
    public async Task Concurrent_first_use_runs_one_device_flow_and_shares_the_committed_credential()
    {
        var handler = new ConcurrentLoginHandler();
        var store = new CredentialStore(
            Path.Combine(_root, "concurrent-first-use"), new TestProtector());
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance, _ => { });
        using var auth = new AuthService(
            factory,
            credentials,
            _time,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: false,
            ownsCredentialService: true);

        var first = auth.EnsureGitHubTokenAsync(CancellationToken.None).AsTask();
        await handler.FirstDeviceCodeRequest.WaitAsync(TimeSpan.FromSeconds(5));
        var second = auth.EnsureGitHubTokenAsync(CancellationToken.None).AsTask();
        handler.ReleaseFirstDeviceCodeResponse();

        var tokens = await Task.WhenAll(first, second);

        Assert.All(tokens, token => Assert.Equal(PluginToken, token));
        Assert.Equal(1, handler.DeviceCodeRequests);
        Assert.Equal(1, handler.TokenRequests);
    }

    [Fact]
    public async Task Explicit_login_replaces_working_version_two_with_version_three()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"device_code":"device-secret","user_code":"ABCD-EFGH",
                 "verification_uri":"https://github.com/login/device","expires_in":900,"interval":-1}
                """),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"" + PluginToken + "\",\"token_type\":\"bearer\"}"),
        ]));
        var store = new CredentialStore(
            Path.Combine(_root, "replace-v1"), new TestProtector());
        store.Save(DirectRecord(DirectToken));
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance, _ => { });
        using var auth = new AuthService(
            factory, credentials, _time, NullLoggerFactory.Instance,
            enableBackgroundRefresh: false, ownsCredentialService: true);

        _ = await auth.LoginAsync(CancellationToken.None);

        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion, loaded.Version);
        Assert.Equal(CopilotPluginClientId, loaded.OAuthClientId);
        Assert.Equal(PluginToken, loaded.AccessToken);
        Assert.Equal(1, loaded.Generation);
    }

    [Fact]
    public async Task Version_three_credential_exchanges_for_a_Copilot_lease()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"token":"copilot-exchanged","expires_at":2000000000,"refresh_in":1500,
                 "endpoints":{"api":"https://api.githubcopilot.com"}}
                """),
        ]));
        var store = new CredentialStore(
            Path.Combine(_root, "version-three-exchange"), new TestProtector());
        store.Save(PluginRecord());
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(
            factory, credentials, _time, NullLoggerFactory.Instance,
            enableBackgroundRefresh: false, ownsCredentialService: true);

        var lease = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        Assert.Equal("copilot-exchanged", lease.Token);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion,
            lease.CredentialVersion);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/copilot_internal/v2/token", request.Uri.AbsolutePath);
        Assert.Equal(PluginToken, request.AuthorizationParameter);
    }

    [Fact]
    public async Task Version_three_refresh_uses_recorded_client_id_and_preserves_version()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"ghu_rotated\",\"expires_in\":28800,"
                + "\"refresh_token\":\"ghr_rotated\",\"refresh_token_expires_in\":15811200}"),
        ]));
        var store = new CredentialStore(
            Path.Combine(_root, "version-three-refresh"), new TestProtector());
        store.Save(PluginRecord() with
        {
            AccessTokenExpiresAt = _time.GetUtcNow(),
            RefreshToken = PluginRefreshToken,
            RefreshTokenExpiresAt = _time.GetUtcNow().AddDays(30),
        });
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        using var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance);

        var lease = await credentials.GetUsableAsync(CancellationToken.None);

        Assert.Equal("ghu_rotated", lease.AccessToken);
        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(CopilotPluginClientId,
            body.RootElement.GetProperty("client_id").GetString());
        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion, loaded.Version);
        Assert.Equal(CopilotPluginClientId, loaded.OAuthClientId);
        Assert.Equal(2, loaded.Generation);
    }

    [Fact]
    public async Task Version_three_exchange_401_refreshes_with_recorded_client_id_and_replays_once()
    {
        // Contract: a Copilot-token exchange 401 for version 3 performs one OAuth
        // refresh through the persisted provider, preserves version 3, and replays
        // the Copilot-token exchange exactly once with the rotated access token.
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"ghu_rotated\",\"expires_in\":28800,"
                + "\"refresh_token\":\"ghr_rotated\",\"refresh_token_expires_in\":15811200}"),
            Json(HttpStatusCode.OK, """
                {"token":"copilot-recovered","expires_at":2000000000,"refresh_in":1500,
                 "endpoints":{"api":"https://api.githubcopilot.com"}}
                """),
        ]));
        var store = new CredentialStore(
            Path.Combine(_root, "version-three-exchange-401"), new TestProtector());
        store.Save(PluginRecord() with
        {
            RefreshToken = PluginRefreshToken,
            RefreshTokenExpiresAt = _time.GetUtcNow().AddDays(30),
        });
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(
            factory, credentials, _time, NullLoggerFactory.Instance,
            enableBackgroundRefresh: false, ownsCredentialService: true);

        var lease = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        Assert.Equal("copilot-recovered", lease.Token);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion,
            lease.CredentialVersion);
        Assert.Equal(2, lease.CredentialGeneration);

        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(2, requests.Count(request =>
            request.Uri.AbsolutePath == "/copilot_internal/v2/token"));
        Assert.Equal(PluginToken, requests[0].AuthorizationParameter);
        Assert.Equal("/login/oauth/access_token", requests[1].Uri.AbsolutePath);
        using var refreshBody = JsonDocument.Parse(requests[1].Body);
        Assert.Equal(PluginRefreshToken,
            refreshBody.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal(CopilotPluginClientId,
            refreshBody.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("refresh_token",
            refreshBody.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("ghu_rotated", requests[2].AuthorizationParameter);

        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion, loaded.Version);
        Assert.Equal(CopilotPluginClientId, loaded.OAuthClientId);
        Assert.Equal("ghu_rotated", loaded.AccessToken);
        Assert.Equal(2, loaded.Generation);
    }

    [Fact]
    public void Version_three_without_oauth_client_id_is_rejected()
    {
        var store = new CredentialStore(
            Path.Combine(_root, "version-three-missing-provider"), new TestProtector());

        var error = Assert.Throws<InvalidOperationException>(() =>
            store.Save(PluginRecord() with { OAuthClientId = null }));

        Assert.Contains("OAuth client id", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Version_three_with_another_oauth_client_id_is_rejected()
    {
        var store = new CredentialStore(
            Path.Combine(_root, "version-three-wrong-provider"), new TestProtector());

        var error = Assert.Throws<InvalidOperationException>(() =>
            store.Save(PluginRecord() with { OAuthClientId = "178c6fc778ccc68e1d6a" }));

        Assert.Contains("unsupported OAuth client id", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Version_two_credential_publishes_direct_CAPI_lease_without_exchange()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>());
        using var auth = CreateAuth(handler, DirectToken);

        var lease = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        Assert.Equal(DirectToken, lease.Token);
        Assert.Equal("https://api.githubcopilot.com", lease.ApiBaseUrl);
        Assert.Equal(DateTimeOffset.MaxValue, lease.RefreshAt);
        Assert.Equal(DateTimeOffset.MaxValue, lease.ServerExpiresAt);
        Assert.Equal(1, lease.Generation);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Direct_CAPI_lease_does_not_arm_background_refresh_timer()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>());
        var time = new CountingTimeProvider(_time.GetUtcNow());
        var store = new CredentialStore(
            Path.Combine(_root, "direct-no-timer"), new TestProtector());
        store.Save(DirectRecord(DirectToken));
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, time, NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(
            factory,
            credentials,
            time,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: true,
            ownsCredentialService: true);

        _ = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        Assert.Equal(0, time.TimerCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Forbidden_direct_lease_republishes_once_without_token_exchange()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>());
        using var auth = CreateAuth(handler, DirectToken);
        var first = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        var second = await auth.GetCopilotTokenAsync(
            new CopilotLeaseRejection(first, CopilotLeaseRejectionReason.Forbidden),
            CancellationToken.None);

        Assert.Equal(DirectToken, second.Token);
        Assert.Equal("https://api.githubcopilot.com", second.ApiBaseUrl);
        Assert.Equal(2, second.Generation);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_direct_lease_requires_relogin_without_token_exchange()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>());
        using var auth = CreateAuth(handler, DirectToken);
        var first = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(
                new CopilotLeaseRejection(first, CopilotLeaseRejectionReason.Unauthorized),
                CancellationToken.None).AsTask());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Late_unauthorized_direct_lease_rejects_the_same_republished_credential()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>());
        using var auth = CreateAuth(handler, DirectToken);
        var first = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);
        var republished = await auth.GetCopilotTokenAsync(
            new CopilotLeaseRejection(first, CopilotLeaseRejectionReason.Forbidden),
            CancellationToken.None);
        Assert.Equal(first.Token, republished.Token);
        Assert.NotEqual(first.Generation, republished.Generation);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(
                new CopilotLeaseRejection(first, CopilotLeaseRejectionReason.Unauthorized),
                CancellationToken.None).AsTask());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ordinary_caller_cannot_reuse_a_cached_terminal_direct_credential()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>());
        var store = new CredentialStore(
            Path.Combine(_root, "terminal-direct-fast-path"), new TestProtector());
        store.Save(DirectRecord(DirectToken));
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(
            factory,
            credentials,
            _time,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: false,
            ownsCredentialService: true);
        var cached = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);
        credentials.MarkTerminal(
            cached.CredentialVersion,
            cached.CredentialId,
            cached.CredentialGeneration);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(ct: CancellationToken.None).AsTask());

        Assert.Null(auth.CopilotApiBaseUrl);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Late_old_401_cannot_reuse_a_cached_terminal_new_identity()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>());
        var store = new CredentialStore(
            Path.Combine(_root, "stale-401-terminal-cache"), new TestProtector());
        store.Save(DirectRecord(DirectToken));
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(
            factory,
            credentials,
            _time,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: false,
            ownsCredentialService: true);
        var current = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);
        credentials.MarkTerminal(
            current.CredentialVersion,
            current.CredentialId,
            current.CredentialGeneration);
        var stale = current with
        {
            Token = "gho_stale_identity",
            CredentialId = "stale-id",
            Generation = 0,
        };

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(
                new CopilotLeaseRejection(stale, CopilotLeaseRejectionReason.Unauthorized),
                CancellationToken.None).AsTask());

        Assert.Null(auth.CopilotApiBaseUrl);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stale_rejection_cannot_clear_the_current_terminal_credential_identity()
    {
        var store = new CredentialStore(
            Path.Combine(_root, "stale-terminal-identity"), new TestProtector());
        store.Save(DirectRecord(DirectToken));
        var factory = new SingleClientHttpClientFactory(
            new HttpClient(new CaptureHandler(new Queue<HttpResponseMessage>())));
        using var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance);

        credentials.MarkTerminal(
            CredentialFileRecord.GitHubCliOAuthVersion, "direct-id", generation: 1);
        credentials.MarkTerminal(
            CredentialFileRecord.GitHubCliOAuthVersion, "stale-id", generation: 1);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            credentials.GetUsableAsync(CancellationToken.None).AsTask());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AuthService CreateAuth(CaptureHandler handler, string token)
    {
        var store = new CredentialStore(
            Path.Combine(_root, Guid.NewGuid().ToString("N")), new TestProtector());
        store.Save(DirectRecord(token));
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance);
        return new AuthService(
            factory,
            credentials,
            _time,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: false,
            ownsCredentialService: true);
    }

    private static CredentialFileRecord DirectRecord(string token) => new()
    {
        Version = CredentialFileRecord.GitHubCliOAuthVersion,
        AccessToken = token,
        CredentialId = "direct-id",
        Generation = 1,
    };

    private static CredentialFileRecord PluginRecord() => new()
    {
        Version = CredentialFileRecord.CopilotPluginExplicitProviderVersion,
        AccessToken = PluginToken,
        OAuthClientId = CopilotPluginClientId,
        CredentialId = "plugin-id",
        Generation = 1,
    };

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Split('=', 2))
            .ToDictionary(
                field => Uri.UnescapeDataString(field[0].Replace('+', ' ')),
                field => Uri.UnescapeDataString(field[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class CaptureHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new CapturedRequest(
                request.RequestUri!,
                request.Content?.Headers.ContentType?.MediaType,
                body,
                request.Headers.Authorization?.Parameter));
            if (responses.Count == 0)
                throw new InvalidOperationException("Unexpected HTTP request.");
            return responses.Dequeue();
        }
    }

    private sealed class ConcurrentLoginHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstDeviceCodeRequest = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstDeviceCodeResponse = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deviceCodeRequests;
        private int _tokenRequests;

        public Task FirstDeviceCodeRequest => _firstDeviceCodeRequest.Task;
        public int DeviceCodeRequests => Volatile.Read(ref _deviceCodeRequests);
        public int TokenRequests => Volatile.Read(ref _tokenRequests);

        public void ReleaseFirstDeviceCodeResponse() =>
            _releaseFirstDeviceCodeResponse.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/login/device/code")
            {
                var count = Interlocked.Increment(ref _deviceCodeRequests);
                if (count == 1)
                {
                    _firstDeviceCodeRequest.TrySetResult();
                    await _releaseFirstDeviceCodeResponse.Task.WaitAsync(cancellationToken);
                }

                return Json(HttpStatusCode.OK, $$"""
                    {
                      "device_code":"device-secret-{{count}}",
                      "user_code":"ABCD-EFGH",
                      "verification_uri":"https://github.com/login/device",
                      "expires_in":900,
                      "interval":-1
                    }
                    """);
            }

            Interlocked.Increment(ref _tokenRequests);
            return Json(HttpStatusCode.OK,
                "{\"access_token\":\"" + PluginToken + "\",\"token_type\":\"bearer\"}");
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string? ContentType,
        string Body,
        string? AuthorizationParameter);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public int TimerCount { get; private set; }
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            TimerCount++;
            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) =>
            [0xB7, .. plaintext.Select(value => (byte)(value ^ 0x27))];

        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length == 0 || blob[0] != 0xB7)
                throw new System.Security.Cryptography.CryptographicException();
            return blob[1..].Select(value => (byte)(value ^ 0x27)).ToArray();
        }
    }
}

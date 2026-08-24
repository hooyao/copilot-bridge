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
/// Contract: fresh bridge authentication implements GitHub CLI's reviewed OAuth
/// device flow in-process, and the resulting gho_ credential authenticates directly
/// to Copilot CAPI without an intermediate token exchange or gh executable.
/// </summary>
public sealed class GitHubCliOAuthContractTests : IDisposable
{
    private const string GitHubCliClientId = "178c6fc778ccc68e1d6a";
    private const string GitHubCliScopes = "repo read:org gist";
    private const string DirectToken = "gho_DIRECT_CREDENTIAL_DO_NOT_LOG";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-gh-cli-oauth-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _time = new(
        new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Device_code_request_matches_GitHub_CLI_OAuth_contract()
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
        Assert.Equal(GitHubCliClientId, fields["client_id"]);
        Assert.Equal(GitHubCliScopes, fields["scope"]);
        Assert.DoesNotContain("client_secret", fields.Keys);
    }

    [Fact]
    public async Task Device_token_poll_matches_GitHub_CLI_OAuth_contract()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"" + DirectToken
                + "\",\"token_type\":\"bearer\",\"scope\":\"repo,read:org,gist\"}"),
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

        Assert.Equal(DirectToken, token.AccessToken);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://github.com/login/oauth/access_token", request.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        var fields = ParseForm(request.Body);
        Assert.Equal(GitHubCliClientId, fields["client_id"]);
        Assert.Equal("device-secret", fields["device_code"]);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", fields["grant_type"]);
        Assert.DoesNotContain("client_secret", fields.Keys);
    }

    [Fact]
    public async Task Fresh_login_commits_version_two_to_the_single_exe_local_file()
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
                "{\"access_token\":\"" + DirectToken
                + "\",\"token_type\":\"bearer\",\"scope\":\"repo,read:org,gist\"}"),
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

        Assert.Equal(DirectToken, token);
        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));
        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(CredentialFileRecord.GitHubCliOAuthVersion, loaded.Version);
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

        Assert.All(tokens, token => Assert.Equal(DirectToken, token));
        Assert.Equal(1, handler.DeviceCodeRequests);
        Assert.Equal(1, handler.TokenRequests);
    }

    [Fact]
    public async Task Explicit_login_replaces_working_version_one_with_version_two()
    {
        var handler = new CaptureHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"device_code":"device-secret","user_code":"ABCD-EFGH",
                 "verification_uri":"https://github.com/login/device","expires_in":900,"interval":-1}
                """),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"" + DirectToken + "\",\"token_type\":\"bearer\"}"),
        ]));
        var store = new CredentialStore(
            Path.Combine(_root, "replace-v1"), new TestProtector());
        store.Save(new CredentialFileRecord
        {
            Version = CredentialFileRecord.CopilotPluginVersion,
            AccessToken = "ghu_still_working",
            CredentialId = "legacy-id",
            Generation = 8,
        });
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory, store, _time, NullLogger<CredentialService>.Instance, _ => { });
        using var auth = new AuthService(
            factory, credentials, _time, NullLoggerFactory.Instance,
            enableBackgroundRefresh: false, ownsCredentialService: true);

        _ = await auth.LoginAsync(CancellationToken.None);

        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(CredentialFileRecord.GitHubCliOAuthVersion, loaded.Version);
        Assert.Equal(DirectToken, loaded.AccessToken);
        Assert.Equal(1, loaded.Generation);
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
                body));
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
                "{\"access_token\":\"" + DirectToken + "\",\"token_type\":\"bearer\"}");
        }
    }

    private sealed record CapturedRequest(Uri Uri, string? ContentType, string Body);

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

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: a refreshable GitHub credential renews before expiry and once after
/// rejection. Refresh-token rotation is single-flight and a non-refreshable or
/// rejected refresh credential terminates with an actionable re-login failure.
/// </summary>
public sealed class GitHubCredentialManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-github-refresh-{Guid.NewGuid():N}");
    private readonly TestProtector _protector = new();
    private readonly ManualTimeProvider _time = new(
        new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Credential_inside_five_minute_window_rotates_and_persists_both_tokens()
    {
        var store = CreateStore();
        store.SaveNew(RefreshableRecord(generation: 4, accessExpiresIn: TimeSpan.FromMinutes(4)));
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"access_token":"ghu_new","expires_in":3600,"refresh_token":"ghr_new","refresh_token_expires_in":7200,"token_type":"bearer","scope":"read:user"}
                """),
        ]));
        var manager = CreateManager(store, handler);

        var credential = await manager.GetUsableAsync(CancellationToken.None);

        Assert.Equal("ghu_new", credential.AccessToken);
        Assert.Equal("ghr_new", credential.RefreshToken);
        Assert.Equal(5, credential.Generation);
        Assert.Equal(_time.GetUtcNow().AddHours(1), credential.AccessTokenExpiresAt);
        Assert.Equal(_time.GetUtcNow().AddHours(2), credential.RefreshTokenExpiresAt);
        Assert.Single(handler.Requests);
        var request = handler.Requests.Single();
        Assert.Contains("\"grant_type\":\"refresh_token\"", request.Body);
        Assert.Contains("\"refresh_token\":\"ghr_old\"", request.Body);
        Assert.DoesNotContain("client_secret", request.Body, StringComparison.OrdinalIgnoreCase);

        var persisted = store.TryLoad();
        Assert.NotNull(persisted);
        Assert.Equal(credential, persisted.Record);
        Assert.Equal("ghu_new", _protector.ReadPlaintext(File.ReadAllBytes(store.LegacyPrimaryPath)));
    }

    [Fact]
    public async Task Credential_outside_window_is_reused_without_network()
    {
        var store = CreateStore();
        store.SaveNew(RefreshableRecord(generation: 1, accessExpiresIn: TimeSpan.FromHours(1)));
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>());
        var manager = CreateManager(store, handler);

        var credential = await manager.GetUsableAsync(CancellationToken.None);

        Assert.Equal(1, credential.Generation);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Non_expiring_legacy_credential_is_reused_without_network()
    {
        var store = CreateStore();
        store.SaveLegacy("ghu_legacy");
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>());
        var manager = CreateManager(store, handler);

        var credential = await manager.GetUsableAsync(CancellationToken.None);

        Assert.Equal("ghu_legacy", credential.AccessToken);
        Assert.False(credential.IsRefreshable);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejected_current_generation_refreshes_once()
    {
        var store = CreateStore();
        store.SaveNew(RefreshableRecord(generation: 7, accessExpiresIn: TimeSpan.FromHours(1)));
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"access_token":"ghu_after_401","expires_in":3600,"refresh_token":"ghr_after_401","refresh_token_expires_in":7200}
                """),
        ]));
        var manager = CreateManager(store, handler);

        var credential = await manager.RefreshAfterRejectionAsync(
            rejectedGeneration: 7,
            CancellationToken.None);

        Assert.Equal("ghu_after_401", credential.AccessToken);
        Assert.Equal(8, credential.Generation);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rejected_stale_generation_reuses_newer_record_without_refresh()
    {
        var store = CreateStore();
        store.SaveNew(RefreshableRecord(generation: 8, accessExpiresIn: TimeSpan.FromHours(1)));
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>());
        var manager = CreateManager(store, handler);

        var credential = await manager.RefreshAfterRejectionAsync(
            rejectedGeneration: 7,
            CancellationToken.None);

        Assert.Equal(8, credential.Generation);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Concurrent_expiry_callers_share_one_rotation()
    {
        var store = CreateStore();
        store.SaveNew(RefreshableRecord(generation: 2, accessExpiresIn: TimeSpan.FromMinutes(1)));
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"access_token":"ghu_shared","expires_in":3600,"refresh_token":"ghr_shared","refresh_token_expires_in":7200}
                """),
        ]));
        var manager = CreateManager(store, handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => manager.GetUsableAsync(CancellationToken.None).AsTask()));

        Assert.All(results, result => Assert.Equal("ghu_shared", result.AccessToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Two_manager_instances_sharing_one_path_consume_refresh_token_once()
    {
        var firstStore = CreateStore();
        firstStore.SaveNew(RefreshableRecord(
            generation: 10,
            accessExpiresIn: TimeSpan.FromMinutes(1)));
        var secondStore = CreateStore();
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"access_token":"ghu_cross_process","expires_in":3600,"refresh_token":"ghr_cross_process","refresh_token_expires_in":7200}
                """),
        ]), TimeSpan.FromMilliseconds(200));
        using var first = CreateManager(firstStore, handler);
        using var second = CreateManager(secondStore, handler);

        var results = await Task.WhenAll(
            first.GetUsableAsync(CancellationToken.None).AsTask(),
            second.GetUsableAsync(CancellationToken.None).AsTask());

        Assert.All(results, result =>
        {
            Assert.Equal(11, result.Generation);
            Assert.Equal("ghu_cross_process", result.AccessToken);
        });
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rejected_legacy_token_requires_interactive_login_without_retry_loop()
    {
        var store = CreateStore();
        store.SaveLegacy("ghu_legacy_bad");
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>());
        var manager = CreateManager(store, handler);

        var error = await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            manager.RefreshAfterRejectionAsync(0, CancellationToken.None).AsTask());

        Assert.Contains("auth logout", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth login", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
        Assert.Equal("ghu_legacy_bad", store.TryLoad()!.Record.AccessToken);
    }

    [Fact]
    public async Task Rejected_refresh_token_preserves_committed_record_and_terminates()
    {
        var store = CreateStore();
        var original = RefreshableRecord(generation: 3, accessExpiresIn: TimeSpan.FromMinutes(1));
        store.SaveNew(original);
        var handler = new OAuthHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """
                {"error":"bad_refresh_token","error_description":"refresh token rejected"}
                """),
        ]));
        var manager = CreateManager(store, handler);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            manager.GetUsableAsync(CancellationToken.None).AsTask());

        Assert.Single(handler.Requests);
        Assert.Equal(original, store.TryLoad()!.Record);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private GitHubCredentialStore CreateStore() => new(
        Path.Combine(_root, "primary"),
        Path.Combine(_root, "fallback"),
        _protector);

    private GitHubCredentialManager CreateManager(
        GitHubCredentialStore store,
        HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new GitHubCredentialManager(
            new SingleClientHttpClientFactory(http),
            store,
            _time,
            NullLogger<GitHubCredentialManager>.Instance);
    }

    private GitHubCredentialRecord RefreshableRecord(long generation, TimeSpan accessExpiresIn) => new()
    {
        FormatVersion = GitHubCredentialRecord.CurrentFormatVersion,
        AccessToken = "ghu_old",
        AccessTokenExpiresAt = _time.GetUtcNow().Add(accessExpiresIn),
        RefreshToken = "ghr_old",
        RefreshTokenExpiresAt = _time.GetUtcNow().AddDays(30),
        TokenType = "bearer",
        Scope = "read:user",
        Generation = generation,
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class OAuthHandler(
        Queue<HttpResponseMessage> responses,
        TimeSpan? responseDelay = null) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new CapturedRequest(request.Method, request.RequestUri!, body));
            HttpResponseMessage response;
            lock (_gate)
            {
                if (responses.Count == 0)
                    throw new InvalidOperationException("Unexpected OAuth request.");
                response = responses.Dequeue();
            }
            if (responseDelay is { } delay)
                await Task.Delay(delay, cancellationToken);
            return response;
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestProtector : ITokenProtector
    {
        private static readonly byte[] Prefix = "cipher:"u8.ToArray();

        public byte[] Protect(byte[] plaintext)
        {
            var result = new byte[Prefix.Length + plaintext.Length];
            Prefix.CopyTo(result, 0);
            for (var i = 0; i < plaintext.Length; i++)
                result[Prefix.Length + i] = (byte)(plaintext[plaintext.Length - 1 - i] ^ 0x5A);
            return result;
        }

        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length < Prefix.Length || !blob.AsSpan(0, Prefix.Length).SequenceEqual(Prefix))
                throw new System.Security.Cryptography.CryptographicException("invalid test credential");
            var result = new byte[blob.Length - Prefix.Length];
            for (var i = 0; i < result.Length; i++)
                result[i] = (byte)(blob[blob.Length - 1 - i] ^ 0x5A);
            return result;
        }

        public string ReadPlaintext(byte[] blob) => Encoding.UTF8.GetString(Unprotect(blob));
    }
}

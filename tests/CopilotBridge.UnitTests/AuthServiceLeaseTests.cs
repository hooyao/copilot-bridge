using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: a Copilot bearer, endpoint, deadlines, and generation are one
/// immutable lease. Refresh scheduling follows refresh_in relative to receipt
/// time, and rejection refreshes only the generation that actually failed.
/// </summary>
public sealed class AuthServiceLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-lease-contract-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _time = new(
        new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Lease_uses_receipt_relative_refresh_deadline_despite_past_server_expiry()
    {
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            CopilotToken("copilot-one", "https://api.one.test", expiresAt: 1, refreshIn: 1500),
        ]));
        using var auth = CreateAuth(handler);

        var lease = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        Assert.Equal("copilot-one", lease.Token);
        Assert.Equal("https://api.one.test", lease.ApiBaseUrl);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), lease.ServerExpiresAt);
        Assert.Equal(_time.GetUtcNow().AddMinutes(21), lease.RefreshAt);
        Assert.Equal(1, lease.Generation);
    }

    [Theory]
    [InlineData(CopilotLeaseRejectionReason.Unauthorized)]
    [InlineData(CopilotLeaseRejectionReason.Forbidden)]
    public async Task Rejected_current_lease_refreshes_token_and_endpoint_as_one_generation(
        CopilotLeaseRejectionReason reason)
    {
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            CopilotToken("copilot-one", "https://api.one.test", 2_000_000_000, 1500),
            CopilotToken("copilot-two", "https://api.two.test", 2_000_000_100, 1500),
        ]));
        using var auth = CreateAuth(handler);
        var first = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        var second = await auth.GetCopilotTokenAsync(
            rejection: new CopilotLeaseRejection(first, reason),
            ct: CancellationToken.None);

        Assert.Equal("copilot-two", second.Token);
        Assert.Equal("https://api.two.test", second.ApiBaseUrl);
        Assert.Equal(2, second.Generation);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(CopilotLeaseRejectionReason.Unauthorized)]
    [InlineData(CopilotLeaseRejectionReason.Forbidden)]
    public async Task Rejected_stale_lease_reuses_already_published_generation(
        CopilotLeaseRejectionReason reason)
    {
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            CopilotToken("copilot-one", "https://api.one.test", 2_000_000_000, 1500),
            CopilotToken("copilot-two", "https://api.two.test", 2_000_000_100, 1500),
        ]));
        using var auth = CreateAuth(handler);
        var first = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);
        var second = await auth.GetCopilotTokenAsync(
            new CopilotLeaseRejection(first, reason), CancellationToken.None);

        var reused = await auth.GetCopilotTokenAsync(
            new CopilotLeaseRejection(first, reason), CancellationToken.None);

        Assert.Same(second, reused);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_rejections_single_flight_one_new_lease()
    {
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            CopilotToken("copilot-one", "https://api.one.test", 2_000_000_000, 1500),
            CopilotToken("copilot-two", "https://api.two.test", 2_000_000_100, 1500),
        ]));
        using var auth = CreateAuth(handler);
        var first = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        var leases = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            auth.GetCopilotTokenAsync(
                new CopilotLeaseRejection(
                    first, CopilotLeaseRejectionReason.Unauthorized),
                CancellationToken.None).AsTask()));

        Assert.All(leases, lease =>
        {
            Assert.Equal(2, lease.Generation);
            Assert.Equal("copilot-two", lease.Token);
            Assert.Equal("https://api.two.test", lease.ApiBaseUrl);
        });
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(CopilotLeaseRejectionReason.Unauthorized, "copilot_401")]
    [InlineData(CopilotLeaseRejectionReason.Forbidden, "copilot_403")]
    public async Task RejectionRefresh_LogsStatusSpecificTriggerWithoutCredentialBytes(
        CopilotLeaseRejectionReason reason,
        string expectedTrigger)
    {
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            CopilotToken("first-secret-token", "https://api.one.test", 2_000_000_000, 1500),
            CopilotToken("second-secret-token", "https://api.two.test", 2_000_000_100, 1500),
        ]));
        var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using var auth = CreateAuth(handler, loggerFactory);
        var first = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        _ = await auth.GetCopilotTokenAsync(
            new CopilotLeaseRejection(first, reason), CancellationToken.None);

        var refresh = Assert.Single(provider.Events, entry =>
            Equals(entry.Properties.GetValueOrDefault("Trigger"), expectedTrigger));
        Assert.Equal(2L, refresh.Properties["Generation"]);
        Assert.DoesNotContain("first-secret-token", refresh.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second-secret-token", refresh.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AuthService CreateAuth(
        HttpMessageHandler handler,
        ILoggerFactory? loggerFactory = null)
    {
        var primary = Path.Combine(_root, Guid.NewGuid().ToString("N"), "primary");
        var fallback = Path.Combine(_root, Guid.NewGuid().ToString("N"), "fallback");
        var store = new GitHubCredentialStore(primary, fallback, new TestProtector());
        store.SaveLegacy("ghu_valid");
        return new AuthService(
            new SingleClientHttpClientFactory(new HttpClient(handler)),
            store,
            _time,
            loggerFactory ?? NullLoggerFactory.Instance,
            onDeviceCodeIssued: null,
            enableBackgroundRefresh: false);
    }

    private static HttpResponseMessage CopilotToken(
        string token,
        string api,
        long expiresAt,
        int refreshIn) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""
              {"token":"{{token}}","expires_at":{{expiresAt}},"refresh_in":{{refreshIn}},"endpoints":{"api":"{{api}}"},"sku":"test"}
              """,
            Encoding.UTF8,
            "application/json"),
    };

    private sealed class SequenceHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            lock (_gate)
            {
                if (responses.Count == 0)
                    return Task.FromException<HttpResponseMessage>(
                        new InvalidOperationException("Unexpected auth request."));
                return Task.FromResult(responses.Dequeue());
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) =>
            [0xA5, .. plaintext.Select(value => (byte)(value ^ 0x5A))];

        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length == 0 || blob[0] != 0xA5)
                throw new System.Security.Cryptography.CryptographicException();
            return blob[1..].Select(value => (byte)(value ^ 0x5A)).ToArray();
        }
    }
}

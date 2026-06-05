using System.Net;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins the idempotent-retry behavior of <see cref="CopilotClient.PostMessagesAsync"/>:
/// transient connection-layer failures that occur BEFORE response headers are
/// read are retried (the body was never processed upstream, so re-send is
/// safe), the retry budget is honored, non-transient failures propagate
/// immediately, and a successful send is returned without retry. Uses a fake
/// <see cref="HttpMessageHandler"/> to script the SendAsync outcomes.
/// </summary>
public class CopilotClientRetryTests
{
    // ── TransientUpstreamError classification ────────────────────────────

    [Fact]
    public void Classifier_HttpRequestException_IsTransient()
    {
        Assert.True(TransientUpstreamError.Is(
            new HttpRequestException("net_http_client_execution_error")));
    }

    [Fact]
    public void Classifier_WrappedSocketException_IsTransient()
    {
        var inner = new System.Net.Sockets.SocketException(10054); // connection reset
        Assert.True(TransientUpstreamError.Is(new HttpRequestException("boom", inner)));
    }

    [Fact]
    public void Classifier_AuthenticationException_IsTransient()
    {
        // net_http_ssl_connection_failed surfaces as AuthenticationException.
        Assert.True(TransientUpstreamError.Is(
            new System.Security.Authentication.AuthenticationException("handshake failed")));
    }

    [Fact]
    public void Classifier_PlainInvalidOperation_IsNotTransient()
    {
        Assert.False(TransientUpstreamError.Is(new InvalidOperationException("bug")));
    }

    // ── PostMessagesAsync retry behavior ─────────────────────────────────

    [Fact]
    public async Task PostMessages_TransientThenSuccess_RetriesAndReturns()
    {
        // Fail twice transiently, then succeed. MaxRetries=2 → 3 total sends.
        var handler = new ScriptedHandler(
        [
            () => throw new HttpRequestException("net_http_client_execution_error"),
            () => throw new HttpRequestException("net_http_client_execution_error"),
            () => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var client = BuildClient(handler, maxRetries: 2);

        using var resp = await client.PostMessagesAsync(SomeBody());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task PostMessages_FirstAttemptSucceeds_NoRetry()
    {
        var handler = new ScriptedHandler(
        [
            () => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var client = BuildClient(handler, maxRetries: 2);

        using var resp = await client.PostMessagesAsync(SomeBody());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task PostMessages_TransientBeyondBudget_Throws()
    {
        // Always fail transiently; MaxRetries=2 → 3 attempts, then propagate.
        var handler = new ScriptedHandler(Enumerable.Repeat<Func<HttpResponseMessage>>(
            () => throw new HttpRequestException("net_http_client_execution_error"), 10).ToArray());
        var client = BuildClient(handler, maxRetries: 2);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostMessagesAsync(SomeBody()).AsTask());

        Assert.Equal(3, handler.CallCount);   // 1 initial + 2 retries
    }

    [Fact]
    public async Task PostMessages_NonTransientError_NoRetry()
    {
        var handler = new ScriptedHandler(
        [
            () => throw new InvalidOperationException("genuine bug"),
            () => new HttpResponseMessage(HttpStatusCode.OK),   // never reached
        ]);
        var client = BuildClient(handler, maxRetries: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PostMessagesAsync(SomeBody()).AsTask());

        Assert.Equal(1, handler.CallCount);   // not retried
    }

    [Fact]
    public async Task PostMessages_RetriesDisabled_ThrowsOnFirstTransient()
    {
        var handler = new ScriptedHandler(
        [
            () => throw new HttpRequestException("net_http_client_execution_error"),
            () => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var client = BuildClient(handler, maxRetries: 0);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostMessagesAsync(SomeBody()).AsTask());

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task PostMessages_HttpErrorStatus_NotRetried()
    {
        // A 502 RESPONSE (not an exception) means headers were received — the
        // request reached upstream, so it is NOT retried. The client returns
        // the response as-is and the endpoint maps it.
        var handler = new ScriptedHandler(
        [
            () => new HttpResponseMessage(HttpStatusCode.BadGateway),
            () => new HttpResponseMessage(HttpStatusCode.OK),   // never reached
        ]);
        var client = BuildClient(handler, maxRetries: 2);

        using var resp = await client.PostMessagesAsync(SomeBody());

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static ReadOnlyMemory<byte> SomeBody() =>
        System.Text.Encoding.UTF8.GetBytes("""{"model":"claude-opus-4.8","messages":[]}""");

    private static CopilotClient BuildClient(ScriptedHandler handler, int maxRetries)
    {
        var http = new HttpClient(handler);
        var opts = Options.Create(new UpstreamRetryOptions
        {
            MaxRetries = maxRetries,
            BaseDelayMs = 1,            // keep tests fast
            BackoffMultiplier = 1.0,
            MaxDelayMs = 2,
        });
        return new CopilotClient(
            http, new FakeAuth(), new CopilotHeaderFactory(), opts,
            NullLogger<CopilotClient>.Instance);
    }

    /// <summary>HttpMessageHandler that returns/throws per a scripted list, one per call.</summary>
    private sealed class ScriptedHandler(Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var idx = CallCount;
            CallCount++;
            var action = idx < script.Length ? script[idx] : script[^1];
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    /// <summary>Minimal IAuthService that returns a fixed token + base URL without network.</summary>
    private sealed class FakeAuth : IAuthService
    {
        public bool IsAuthenticated => true;
        public string TokenLocation => "(test)";
        public string? CopilotApiBaseUrl => "https://api.test.githubcopilot.com";
        public DateTimeOffset? CopilotTokenExpiry => DateTimeOffset.MaxValue;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult("gh-token");
        public ValueTask<string> GetCopilotTokenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult("test-token");
        public void SignOut() { }
    }
}

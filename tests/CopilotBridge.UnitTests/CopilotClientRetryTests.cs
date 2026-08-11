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
    [Fact]
    public async Task Downstream_provider_sentinel_can_never_replace_AuthService_upstream_token()
    {
        const string realToken = "copilot-real-upstream-token-do-not-leak";
        var handler = new AuthorizationCaptureHandler();
        var client = BuildClient(handler, maxRetries: 0, auth: new FixedAuth(realToken));

        _ = await client.GetModelsAsync();
        using var responses = await client.PostResponsesAsync(
            System.Text.Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(["Bearer " + realToken, "Bearer " + realToken], handler.Authorizations);
        Assert.DoesNotContain(handler.Authorizations,
            value => value.Contains(AuthCommand.ProviderSentinel, StringComparison.Ordinal));
    }

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

    // ── Authentication 401 replay contract ──────────────────────────────

    [Fact]
    public async Task PostResponses_First401_RefreshesLeaseAndReplaysExactBytesOnce()
    {
        var rejectedContent = new DisposalTrackingContent();
        var handler = new CapturingScriptedHandler([
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = rejectedContent },
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var auth = new RotatingAuth();
        var client = BuildClient(handler, maxRetries: 0, auth: auth);
        var body = System.Text.Encoding.UTF8.GetBytes("{\"model\":\"gpt-5.6-sol\",\"stream\":true}");

        using var response = await client.PostResponsesAsync(body, vision: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, auth.RejectionCount);
        var requests = handler.Requests.ToArray();
        Assert.Equal("Bearer old-copilot-token", requests[0].Authorization);
        Assert.Equal("Bearer new-copilot-token", requests[1].Authorization);
        Assert.Equal("https://api.old.test/responses", requests[0].Uri.AbsoluteUri);
        Assert.Equal("https://api.new.test/responses", requests[1].Uri.AbsoluteUri);
        Assert.Equal(body, requests[0].Body);
        Assert.Equal(body, requests[1].Body);
        Assert.Equal("true", requests[0].Headers["Copilot-Vision-Request"]);
        Assert.Equal("true", requests[1].Headers["Copilot-Vision-Request"]);
        Assert.True(rejectedContent.Disposed);
    }

    [Fact]
    public async Task PostMessages_First401_PreservesBetaOverridesAndBodyOnReplay()
    {
        var handler = new CapturingScriptedHandler([
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var client = BuildClient(handler, maxRetries: 0, auth: new RotatingAuth());
        var body = System.Text.Encoding.UTF8.GetBytes("{\"model\":\"claude-opus-5\",\"stream\":true}");
        var overrides = new Dictionary<string, string?>
        {
            ["Editor-Version"] = "vscode/contract-test",
        };

        using var response = await client.PostMessagesAsync(
            body,
            vision: true,
            anthropicBeta: ["beta-one", "beta-two"],
            copilotHeaderOverrides: overrides);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal(body, request.Body);
            Assert.Equal("beta-one,beta-two", request.Headers["anthropic-beta"]);
            Assert.Equal("vscode/contract-test", request.Headers["Editor-Version"]);
            Assert.Equal("true", request.Headers["Copilot-Vision-Request"]);
        });
    }

    [Fact]
    public async Task ModelsAndCountTokens_First401_EachRefreshAndReplayOnce()
    {
        var modelHandler = new CapturingScriptedHandler([
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}"),
            },
        ]);
        var modelClient = BuildClient(modelHandler, maxRetries: 0, auth: new RotatingAuth());

        var models = await modelClient.GetModelsAsync();

        Assert.Empty(models.Data);
        Assert.Equal(2, modelHandler.Requests.Count);

        var countHandler = new CapturingScriptedHandler([
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"input_tokens\":7}"),
            },
        ]);
        var countClient = BuildClient(countHandler, maxRetries: 0, auth: new RotatingAuth());
        var body = System.Text.Encoding.UTF8.GetBytes("{\"model\":\"claude-opus-5\"}");

        using var countResponse = await countClient.PostCountTokensAsync(body);

        Assert.Equal(HttpStatusCode.OK, countResponse.StatusCode);
        Assert.Equal(2, countHandler.Requests.Count);
        Assert.All(countHandler.Requests, request => Assert.Equal(body, request.Body));
    }

    [Fact]
    public async Task Second401_IsTerminalAndNeverLoops()
    {
        var handler = new CapturingScriptedHandler([
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var auth = new RotatingAuth();
        var client = BuildClient(handler, maxRetries: 0, auth: auth);

        using var response = await client.PostResponsesAsync(SomeBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, auth.RejectionCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Non401_StatusNeverTriggersAuthReplay(HttpStatusCode status)
    {
        var handler = new CapturingScriptedHandler([
            _ => new HttpResponseMessage(status),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var auth = new RotatingAuth();
        var client = BuildClient(handler, maxRetries: 0, auth: auth);

        using var response = await client.PostResponsesAsync(SomeBody());

        Assert.Equal(status, response.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal(0, auth.RejectionCount);
    }

    [Fact]
    public async Task AuthReplay_DoesNotResetConsumedTransientRetryBudget()
    {
        var handler = new ScriptedHandler([
            () => throw new HttpRequestException("first connection failure"),
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => throw new HttpRequestException("second connection failure"),
            () => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var auth = new RotatingAuth();
        var client = BuildClient(handler, maxRetries: 1, auth: auth);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostResponsesAsync(SomeBody()).AsTask());

        Assert.Equal(3, handler.CallCount);
        Assert.Equal(1, auth.RejectionCount);
    }

    [Fact]
    public async Task AuthReplay_StillAppliesOneFullFirstByteBudgetToReplay()
    {
        var handler = new First401ThenStallHandler();
        var auth = new RotatingAuth();
        var client = BuildClient(
            handler,
            maxRetries: 3,
            timeout: new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 1,
                StreamIdleTimeoutSeconds = 0,
            },
            auth: auth);

        await Assert.ThrowsAsync<UpstreamTimeoutException>(() =>
            client.PostResponsesAsync(SomeBody()).AsTask());

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, auth.RejectionCount);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static ReadOnlyMemory<byte> SomeBody() =>
        System.Text.Encoding.UTF8.GetBytes("""{"model":"claude-opus-4.8","messages":[]}""");

    private static CopilotClient BuildClient(
        HttpMessageHandler handler,
        int maxRetries,
        UpstreamTimeoutOptions? timeout = null,
        IAuthService? auth = null)
    {
        var http = new HttpClient(handler);
        var opts = Options.Create(new UpstreamRetryOptions
        {
            MaxRetries = maxRetries,
            BaseDelayMs = 1,            // keep tests fast
            BackoffMultiplier = 1.0,
            MaxDelayMs = 2,
        });
        // Default: disable the first-byte budget so the existing retry-contract
        // tests exercise only the retry loop, unperturbed by the timeout timer.
        var timeoutOpts = Options.Create(timeout ?? new UpstreamTimeoutOptions
        {
            FirstByteTimeoutSeconds = 0,
            StreamIdleTimeoutSeconds = 0,
        });
        return new CopilotClient(
            new SingleClientHttpClientFactory(http), auth ?? new FakeAuth(), new CopilotHeaderFactory(), opts, timeoutOpts,
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

    private sealed class CapturingScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _callCount;
        public System.Collections.Concurrent.ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
                headers[header.Key] = string.Join(',', header.Value);
            Requests.Enqueue(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString() ?? "",
                body,
                headers));
            var index = Interlocked.Increment(ref _callCount) - 1;
            return script[Math.Min(index, script.Length - 1)](request);
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string Authorization,
        byte[] Body,
        Dictionary<string, string> Headers);

    private sealed class DisposalTrackingContent : StringContent
    {
        public DisposalTrackingContent() : base("unauthorized") { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class First401ThenStallHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
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
        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotAuthLease? rejectedLease = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new CopilotAuthLease
            {
                Token = "test-token", ApiBaseUrl = CopilotApiBaseUrl!,
                RefreshAt = DateTimeOffset.MaxValue, ServerExpiresAt = DateTimeOffset.MaxValue, Generation = 1,
            });
        public void SignOut() { }
    }

    private sealed class FixedAuth(string token) : IAuthService
    {
        public bool IsAuthenticated => true;
        public string TokenLocation => "(test)";
        public string? CopilotApiBaseUrl => "https://api.test.githubcopilot.com";
        public DateTimeOffset? CopilotTokenExpiry => DateTimeOffset.MaxValue;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) => ValueTask.FromResult(token);
        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotAuthLease? rejectedLease = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new CopilotAuthLease
            {
                Token = token, ApiBaseUrl = CopilotApiBaseUrl!,
                RefreshAt = DateTimeOffset.MaxValue, ServerExpiresAt = DateTimeOffset.MaxValue, Generation = 1,
            });
        public void SignOut() { }
    }

    private sealed class RotatingAuth : IAuthService
    {
        private CopilotAuthLease _current = Lease(
            "old-copilot-token", "https://api.old.test", generation: 1);

        public int RejectionCount { get; private set; }
        public bool IsAuthenticated => true;
        public string TokenLocation => "(test)";
        public string? CopilotApiBaseUrl => _current.ApiBaseUrl;
        public DateTimeOffset? CopilotTokenExpiry => _current.ServerExpiresAt;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult("gh-token");

        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotAuthLease? rejectedLease = null,
            CancellationToken ct = default)
        {
            if (rejectedLease?.Generation == _current.Generation)
            {
                RejectionCount++;
                _current = Lease("new-copilot-token", "https://api.new.test", generation: 2);
            }
            return ValueTask.FromResult(_current);
        }

        public void SignOut() { }

        private static CopilotAuthLease Lease(string token, string baseUrl, long generation) => new()
        {
            Token = token,
            ApiBaseUrl = baseUrl,
            RefreshAt = DateTimeOffset.MaxValue,
            ServerExpiresAt = DateTimeOffset.MaxValue,
            Generation = generation,
        };
    }

    private sealed class AuthorizationCaptureHandler : HttpMessageHandler
    {
        public List<string> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            var body = request.Method == HttpMethod.Get
                ? "{\"data\":[]}"
                : "{\"id\":\"resp\",\"object\":\"response\",\"status\":\"completed\",\"output\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }
}

using System.Net;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.Playground.ApiContract;

/// <summary>
/// Captured-real-client-byte contract for authentication replay. This is not a
/// synthetic JSON reconstruction: both sends must carry the exact checked-in
/// Codex request bytes after the first bearer is rejected.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class AuthRejectionReplayContractTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CopilotLeaseRejectionReason.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CopilotLeaseRejectionReason.Forbidden)]
    public async Task CapturedCodexRequest_FirstAuthRejection_ReplaysExactBytesWithoutSecretLog(
        HttpStatusCode status,
        CopilotLeaseRejectionReason expectedReason)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Auth401",
            "codex-request-multiturn-8.json");
        var body = await File.ReadAllBytesAsync(path);
        var handler = new CaptureRejectionThenOkHandler(status);
        var auth = new RotatingAuth();
        var logger = new CaptureLogger();
        var client = new CopilotClient(
            new SingleClientHttpClientFactory(new HttpClient(handler)),
            auth,
            new CopilotHeaderFactory(),
            Options.Create(new UpstreamRetryOptions { MaxRetries = 0 }),
            Options.Create(new UpstreamTimeoutOptions
            {
                FirstByteTimeoutSeconds = 0,
                StreamIdleTimeoutSeconds = 0,
            }),
            logger);

        using var response = await client.PostResponsesAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(body, requests[0].Body);
        Assert.Equal(body, requests[1].Body);
        Assert.Equal("Bearer contract-old-secret", requests[0].Authorization);
        Assert.Equal("Bearer contract-new-secret", requests[1].Authorization);
        Assert.Equal(1, auth.Rejections);
        Assert.Equal(expectedReason, auth.RejectionReason);
        var logs = string.Join('\n', logger.Messages);
        Assert.DoesNotContain("contract-old-secret", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-new-secret", logs, StringComparison.Ordinal);
    }

    private sealed class CaptureRejectionThenOkHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _count;
        public System.Collections.Concurrent.ConcurrentQueue<Captured> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Enqueue(new Captured(
                bytes,
                request.Headers.Authorization?.ToString() ?? ""));
            return Interlocked.Increment(ref _count) == 1
                ? new HttpResponseMessage(status)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record Captured(byte[] Body, string Authorization);

    private sealed class RotatingAuth : IAuthService
    {
        private CopilotAuthLease _lease = NewLease(
            "contract-old-secret", "https://old.test", 1);
        public int Rejections { get; private set; }
        public CopilotLeaseRejectionReason? RejectionReason { get; private set; }
        public bool IsAuthenticated => true;
        public string TokenLocation => "(test)";
        public string? CopilotApiBaseUrl => _lease.ApiBaseUrl;
        public DateTimeOffset? CopilotTokenExpiry => _lease.ServerExpiresAt;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult("github-contract-secret");
        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotLeaseRejection? rejection = null,
            CancellationToken ct = default)
        {
            if (rejection?.Lease.Generation == _lease.Generation)
            {
                Rejections++;
                RejectionReason = rejection.Value.Reason;
                _lease = NewLease("contract-new-secret", "https://new.test", 2);
            }
            return ValueTask.FromResult(_lease);
        }
        public void SignOut() { }

        private static CopilotAuthLease NewLease(
            string token, string baseUrl, long generation) => new()
        {
            Token = token,
            ApiBaseUrl = baseUrl,
            RefreshAt = DateTimeOffset.MaxValue,
            ServerExpiresAt = DateTimeOffset.MaxValue,
            Generation = generation,
        };
    }

    private sealed class CaptureLogger : ILogger<CopilotClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

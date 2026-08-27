using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// Talks to Copilot's authenticated CAPI surfaces. Every send is built from one
/// immutable auth lease. Connection failures before headers use the configured
/// transient budget; a recoverable first HTTP 401 or 403 uses one shared
/// authentication replay.
/// </summary>
internal sealed class CopilotClient(
    IHttpClientFactory httpClientFactory,
    IAuthService auth,
    CopilotHeaderFactory headers,
    IOptions<UpstreamRetryOptions> retryOptions,
    IOptions<UpstreamTimeoutOptions> timeoutOptions,
    ILogger<CopilotClient> log) : ICopilotClient
{
    private readonly UpstreamRetryOptions _retry = retryOptions.Value;
    private readonly UpstreamTimeoutOptions _timeout = timeoutOptions.Value;
#if DEBUG
    private int _testAuthRejectionInjected;
#endif

    public async ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default)
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_FAIL_MODELS"),
                "1",
                StringComparison.Ordinal))
        {
            log.LogWarning("TEST ONLY: forced Copilot /models failure attempt");
            throw new HttpRequestException("Forced Copilot /models failure for behavior testing.");
        }
#endif

        using var response = await SendAuthenticatedAsync(
            lease =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{lease.ApiBaseUrl}/models");
                headers.ApplyTo(
                    request, lease.Token, integrationId: lease.IntegrationId);
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                return request;
            },
            UpstreamHttpClientNames.Metadata,
            "GET /models",
            HttpCompletionOption.ResponseContentRead,
            useFirstByteBudget: false,
            allowTransientRetries: false,
            ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Failed to fetch Copilot models: {(int)response.StatusCode} {response.ReasonPhrase}.");

        return await response.Content.ReadFromJsonAsync(
                   JsonContext.Default.CopilotModelsResponse, ct)
               ?? throw new InvalidOperationException("Empty Copilot models response.");
    }

    public ValueTask<HttpResponseMessage> PostMessagesAsync(
        ReadOnlyMemory<byte> body,
        bool vision = false,
        IReadOnlyList<string>? anthropicBeta = null,
        IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
        CancellationToken ct = default) =>
        SendAuthenticatedAsync(
            lease =>
            {
                var request = JsonPost(
                    $"{lease.ApiBaseUrl}/v1/messages", body);
                headers.ApplyTo(
                    request,
                    lease.Token,
                    vision,
                    copilotHeaderOverrides,
                    lease.IntegrationId);
                if (anthropicBeta is { Count: > 0 })
                    request.Headers.Add(
                        "anthropic-beta", string.Join(',', anthropicBeta));
                return request;
            },
            UpstreamHttpClientNames.Anthropic,
            "POST /v1/messages",
            HttpCompletionOption.ResponseHeadersRead,
            useFirstByteBudget: true,
            allowTransientRetries: true,
            ct);

    public ValueTask<HttpResponseMessage> PostCountTokensAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken ct = default) =>
        SendAuthenticatedAsync(
            lease =>
            {
                var request = JsonPost(
                    $"{lease.ApiBaseUrl}/v1/messages/count_tokens", body);
                headers.ApplyTo(
                    request, lease.Token, integrationId: lease.IntegrationId);
                return request;
            },
            UpstreamHttpClientNames.Metadata,
            "POST /v1/messages/count_tokens",
            HttpCompletionOption.ResponseContentRead,
            useFirstByteBudget: false,
            allowTransientRetries: false,
            ct);

    public ValueTask<HttpResponseMessage> PostResponsesAsync(
        ReadOnlyMemory<byte> body,
        bool vision = false,
        CancellationToken ct = default) =>
        SendAuthenticatedAsync(
            lease =>
            {
                var request = JsonPost($"{lease.ApiBaseUrl}/responses", body);
                headers.ApplyTo(
                    request, lease.Token, vision, integrationId: lease.IntegrationId);
                return request;
            },
            UpstreamHttpClientNames.Responses,
            "POST /responses",
            HttpCompletionOption.ResponseHeadersRead,
            useFirstByteBudget: true,
            allowTransientRetries: true,
            ct);

    private async ValueTask<HttpResponseMessage> SendAuthenticatedAsync(
        Func<CopilotAuthLease, HttpRequestMessage> createRequest,
        string clientName,
        string operation,
        HttpCompletionOption completionOption,
        bool useFirstByteBudget,
        bool allowTransientRetries,
        CancellationToken ct)
    {
        var lease = await auth.GetCopilotTokenAsync(ct: ct);
        var authReplayUsed = false;
        var transientRetriesUsed = 0;

        while (true)
        {
            var request = createRequest(lease);
#if DEBUG
            var injectedResponse = TryInjectOneShotForbidden(
                operation, lease.Generation);
#endif
            HttpResponseMessage response;
            try
            {
#if DEBUG
                if (injectedResponse is not null)
                {
                    response = injectedResponse;
                }
                else
#endif
                {
                    var http = httpClientFactory.CreateClient(clientName);
                    response = useFirstByteBudget
                        ? await SendWithFirstByteBudgetAsync(request, http, ct)
                        : await http.SendAsync(request, completionOption, ct);
                }
            }
            catch (Exception ex) when (
                allowTransientRetries
                && transientRetriesUsed < _retry.MaxRetries
                && !ct.IsCancellationRequested
                && TransientUpstreamError.Is(ex))
            {
                request.Dispose();
                transientRetriesUsed++;
                var delayMs = ComputeBackoffMs(transientRetriesUsed);
                log.LogWarning(
                    "upstream {Operation} transient failure ({Type}: {Message}); "
                    + "retry {Attempt}/{Max} in {DelayMs}ms",
                    operation,
                    ex.GetType().Name,
                    ex.Message,
                    transientRetriesUsed,
                    _retry.MaxRetries,
                    delayMs);
                await Task.Delay(delayMs, ct);
                continue;
            }
            catch
            {
                request.Dispose();
                throw;
            }

            if (!TryGetLeaseRejectionReason(response.StatusCode, out var rejectionReason)
                || authReplayUsed)
            {
                LogTerminalClassification(operation, response.StatusCode, authReplayUsed);
                if (authReplayUsed && response.IsSuccessStatusCode)
                {
                    log.LogInformation(
                        "upstream {Operation} authentication replay outcome=success status={Status}",
                        operation, (int)response.StatusCode);
                }
                return response;
            }

            // A first 401/403 rejects the lease before model work is accepted.
            // Dispose both single-use objects, reject only the generation used,
            // and request a replacement lease. Only a successful replacement is
            // replayed once with a fresh request and the exact business bytes.
            var rejectedStatus = response.StatusCode;
            var rejectedGeneration = lease.Generation;
            response.Dispose();
            request.Dispose();
            log.LogWarning(
                "upstream {Operation} rejected Copilot bearer status={Status} "
                + "generation={Generation} reason={Reason}; resolving authentication recovery",
                operation,
                (int)rejectedStatus,
                rejectedGeneration,
                rejectionReason);
            lease = await auth.GetCopilotTokenAsync(
                new CopilotLeaseRejection(lease, rejectionReason), ct);
            authReplayUsed = true;
            log.LogInformation(
                "upstream {Operation} authentication recovery outcome=replacement_acquired "
                + "rejected_generation={RejectedGeneration} "
                + "replacement_generation={ReplacementGeneration}; replaying once",
                operation,
                rejectedGeneration,
                lease.Generation);
        }
    }

    /// <summary>
    /// Bounds only the wait for response headers. Each connection retry gets the
    /// full configured first-byte budget; the returned response body uses the
    /// caller's cancellation token and is not tied to this temporary CTS.
    /// </summary>
    private async ValueTask<HttpResponseMessage> SendWithFirstByteBudgetAsync(
        HttpRequestMessage request,
        HttpClient http,
        CancellationToken ct)
    {
        var firstByteBudget = _timeout.FirstByteTimeoutSeconds;
        if (firstByteBudget <= 0)
            return await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(firstByteBudget));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UpstreamTimeoutException(
                UpstreamTimeoutPhase.FirstByte,
                TimeSpan.FromSeconds(firstByteBudget));
        }

        timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
        return response;
    }

    private int ComputeBackoffMs(int attempt)
    {
        var raw = _retry.BaseDelayMs * Math.Pow(
            _retry.BackoffMultiplier, attempt - 1);
        return (int)Math.Min(raw, _retry.MaxDelayMs);
    }

    private void LogTerminalClassification(
        string operation,
        HttpStatusCode status,
        bool authReplayUsed)
    {
        var classification = status switch
        {
            HttpStatusCode.Unauthorized when authReplayUsed => "copilot_auth_terminal",
            HttpStatusCode.PaymentRequired => "quota_or_billing",
            HttpStatusCode.Forbidden when authReplayUsed =>
                "policy_or_entitlement_after_auth_replay",
            HttpStatusCode.TooManyRequests => "rate_limit",
            _ => null,
        };
        if (classification is not null)
        {
            log.LogWarning(
                "upstream {Operation} terminal status={Status} classification={Classification}",
                operation, (int)status, classification);
        }
    }

    private static bool TryGetLeaseRejectionReason(
        HttpStatusCode status,
        out CopilotLeaseRejectionReason reason)
    {
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                reason = CopilotLeaseRejectionReason.Unauthorized;
                return true;
            case HttpStatusCode.Forbidden:
                reason = CopilotLeaseRejectionReason.Forbidden;
                return true;
            default:
                reason = default;
                return false;
        }
    }

    private static HttpRequestMessage JsonPost(
        string url,
        ReadOnlyMemory<byte> body) => new(HttpMethod.Post, url)
    {
        Content = new ReadOnlyMemoryContent(body)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
        },
    };

#if DEBUG
    private HttpResponseMessage? TryInjectOneShotForbidden(
        string operation,
        long generation)
    {
        var configuredOperation = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_FORCE_CAPI_403_ONCE");
        var expectedOperation = configuredOperation switch
        {
            "responses" => "POST /responses",
            "messages" => "POST /v1/messages",
            _ => null,
        };
        if (!string.Equals(operation, expectedOperation, StringComparison.Ordinal)
            || Interlocked.CompareExchange(
                ref _testAuthRejectionInjected, 1, 0) != 0)
            return null;

        log.LogWarning(
            "TEST ONLY: injected one-shot CAPI 403 for {Operation} generation={Generation}",
            operation, generation);
        return new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden\n"),
        };
    }
#endif
}

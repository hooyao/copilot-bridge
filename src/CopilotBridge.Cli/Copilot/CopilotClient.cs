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
/// transient budget; an HTTP 401 uses a separate, single authentication replay.
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
            throw new HttpRequestException("Forced Copilot /models failure for behavior testing.");
#endif

        using var response = await SendAuthenticatedAsync(
            lease =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{lease.ApiBaseUrl}/models");
                headers.ApplyTo(request, lease.Token);
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
                    request, lease.Token, vision, copilotHeaderOverrides);
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
                headers.ApplyTo(request, lease.Token);
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
                headers.ApplyTo(request, lease.Token, vision);
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
            MaybeInjectOneShotRejectedBearer(request, operation, lease.Generation);
#endif
            HttpResponseMessage response;
            try
            {
                var http = httpClientFactory.CreateClient(clientName);
                response = useFirstByteBudget
                    ? await SendWithFirstByteBudgetAsync(request, http, ct)
                    : await http.SendAsync(request, completionOption, ct);
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

            if (response.StatusCode != HttpStatusCode.Unauthorized || authReplayUsed)
            {
                LogTerminalClassification(operation, response.StatusCode, authReplayUsed);
                return response;
            }

            // 401 is an authentication rejection: no model work was accepted.
            // Dispose the response and single-use request, reject only the lease
            // used, and replay once with a fresh request object and exact body bytes.
            response.Dispose();
            request.Dispose();
            authReplayUsed = true;
            log.LogWarning(
                "upstream {Operation} rejected Copilot bearer generation={Generation}; "
                + "refreshing and replaying once",
                operation,
                lease.Generation);
            lease = await auth.GetCopilotTokenAsync(lease, ct);
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
            HttpStatusCode.Forbidden => "policy_or_entitlement",
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
    private void MaybeInjectOneShotRejectedBearer(
        HttpRequestMessage request,
        string operation,
        long generation)
    {
        if (!string.Equals(operation, "POST /responses", StringComparison.Ordinal)
            || !string.Equals(
                Environment.GetEnvironmentVariable(
                    "COPILOT_BRIDGE_TEST_REJECT_COPILOT_AUTH_ONCE"),
                "1",
                StringComparison.Ordinal)
            || Interlocked.CompareExchange(
                ref _testAuthRejectionInjected, 1, 0) != 0)
            return;

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", CorruptBearerSignature(request.Headers.Authorization?.Parameter));
        log.LogWarning(
            "TEST ONLY: injected one-shot rejected Copilot bearer for {Operation} "
            + "generation={Generation}",
            operation, generation);
    }

    private static string CorruptBearerSignature(string? token)
    {
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "TEST ONLY auth rejection seam found no bearer to corrupt.");
        var replacement = token[^1] == '0' ? '1' : '0';
        return token[..^1] + replacement;
    }
#endif
}

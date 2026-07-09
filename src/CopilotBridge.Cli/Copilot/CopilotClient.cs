using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Copilot;

internal sealed class CopilotClient(
    HttpClient http,
    IAuthService auth,
    CopilotHeaderFactory headers,
    IOptions<UpstreamRetryOptions> retryOptions,
    IOptions<UpstreamTimeoutOptions> timeoutOptions,
    ILogger<CopilotClient> log) : ICopilotClient
{
    private readonly UpstreamRetryOptions _retry = retryOptions.Value;
    private readonly UpstreamTimeoutOptions _timeout = timeoutOptions.Value;

    public async ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default)
    {
        var (token, baseUrl) = await ResolveAuthAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        headers.ApplyTo(req, token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Failed to fetch Copilot models: {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}");
        }

        return await resp.Content.ReadFromJsonAsync(JsonContext.Default.CopilotModelsResponse, ct)
               ?? throw new InvalidOperationException("Empty Copilot models response.");
    }

    public async ValueTask<HttpResponseMessage> PostMessagesAsync(
        ReadOnlyMemory<byte> body,
        bool vision = false,
        IReadOnlyList<string>? anthropicBeta = null,
        IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
        CancellationToken ct = default)
    {
        var (token, baseUrl) = await ResolveAuthAsync(ct);

        // Idempotent retry of transient connection-layer failures. SendAsync
        // with ResponseHeadersRead throws BEFORE returning a response when the
        // connection can't be established / the TLS handshake fails / the
        // socket is reset before headers arrive — at that point the request
        // body was never processed upstream, so re-sending it is safe. Once
        // SendAsync returns (headers in hand), we DON'T retry: SSE streaming
        // may have started and a re-send would duplicate content. Each attempt
        // builds a fresh HttpRequestMessage (they're single-use).
        var attempt = 0;
        while (true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
            {
                Content = new ReadOnlyMemoryContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
                },
            };
            headers.ApplyTo(req, token, vision, copilotHeaderOverrides);
            if (anthropicBeta is { Count: > 0 })
            {
                req.Headers.Add("anthropic-beta", string.Join(',', anthropicBeta));
            }

            try
            {
                // ResponseHeadersRead so the caller can stream the SSE body
                // without buffering. The caller owns disposal of the result.
                // The first-byte inactivity budget is applied per attempt inside
                // the shared helper (backoff below is outside the armed window).
                return await SendWithFirstByteBudgetAsync(req, ct);
            }
            catch (Exception ex) when (
                attempt < _retry.MaxRetries
                && !ct.IsCancellationRequested
                && TransientUpstreamError.Is(ex))
            {
                req.Dispose();
                attempt++;
                var delayMs = ComputeBackoffMs(attempt);
                log.LogWarning(
                    "upstream POST /v1/messages transient failure ({Type}: {Message}); "
                    + "retry {Attempt}/{Max} in {DelayMs}ms",
                    ex.GetType().Name, ex.Message, attempt, _retry.MaxRetries, delayMs);
                await Task.Delay(delayMs, ct);
            }
            catch
            {
                // Non-transient, or budget exhausted, or cancelled — let it
                // propagate. The request object leaks its handle here only on
                // the throwing path; SendAsync already owns/disposed it on
                // failure, so no explicit Dispose (double-dispose-safe anyway).
                throw;
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="req"/> with <c>ResponseHeadersRead</c>, bounding the
    /// wait for the first byte (response headers) by the first-byte inactivity
    /// budget. Shared by <see cref="PostMessagesAsync"/> and
    /// <see cref="PostResponsesAsync"/> so both forward paths get the same bound.
    /// </summary>
    /// <remarks>
    /// Arms a per-call linked <see cref="CancellationTokenSource"/>: called once per
    /// retry attempt, each fresh send gets the FULL budget (the caller's backoff
    /// runs outside this method). On our timer firing — and only when the caller's
    /// own <paramref name="ct"/> did NOT fire (so a client cancel wins the race) —
    /// throws a terminal <see cref="UpstreamTimeoutException"/> the caller's
    /// transient-retry <c>when</c> clause does not catch. Once headers arrive the
    /// timer is disarmed (<c>CancelAfter(Infinite)</c>) so it cannot fire during the
    /// caller's body read; the caller reads the body with its own <paramref name="ct"/>
    /// (never this method's token), so disposing the linked CTS at method exit does
    /// NOT abort that read — verified by <c>FirstByteCtsLifetimeProbe</c>. The CTS is
    /// disposed on every path (a <c>using</c>) to avoid rooting a per-request
    /// registration on <paramref name="ct"/> for the life of a long-running server.
    /// Budget <c>&lt;= 0</c> ⇒ the original bare send (no CTS, no timer).
    /// </remarks>
    private async ValueTask<HttpResponseMessage> SendWithFirstByteBudgetAsync(
        HttpRequestMessage req, CancellationToken ct)
    {
        var firstByteBudget = _timeout.FirstByteTimeoutSeconds;
        if (firstByteBudget <= 0)
        {
            return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(firstByteBudget));
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UpstreamTimeoutException(
                UpstreamTimeoutPhase.FirstByte, TimeSpan.FromSeconds(firstByteBudget));
        }

        // Headers arrived — disarm so the timer can't fire before the CTS is disposed
        // at method exit; the body read uses the caller's ct, not this token.
        timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
        return resp;
    }

    /// <summary>Exponential backoff for retry attempt N (1-based), clamped to MaxDelayMs.</summary>
    private int ComputeBackoffMs(int attempt)
    {
        var raw = _retry.BaseDelayMs * Math.Pow(_retry.BackoffMultiplier, attempt - 1);
        var clamped = Math.Min(raw, _retry.MaxDelayMs);
        return (int)clamped;
    }

    public async ValueTask<HttpResponseMessage> PostCountTokensAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken ct = default)
    {
        var (token, baseUrl) = await ResolveAuthAsync(ct);

        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages/count_tokens")
        {
            Content = new ReadOnlyMemoryContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        headers.ApplyTo(req, token);

        return await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    public async ValueTask<HttpResponseMessage> PostResponsesAsync(
        ReadOnlyMemory<byte> body,
        bool vision = false,
        CancellationToken ct = default)
    {
        var (token, baseUrl) = await ResolveAuthAsync(ct);

        // Same idempotent transient-retry contract as PostMessagesAsync: retry
        // only connection-layer failures that throw BEFORE SendAsync returns
        // headers (the request body never reached upstream, so re-send is safe);
        // once headers are in hand, never retry (SSE may have started). The Codex
        // backend emits no [DONE] — that's the strategy's concern, not the
        // client's; here we just forward bytes.
        var attempt = 0;
        while (true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new ReadOnlyMemoryContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
                },
            };
            // Endpoint-agnostic official Copilot header set (no anthropic-version);
            // Codex's x-codex-* headers are intentionally NOT forwarded — the
            // bridge presents as the official VS Code client, like /cc.
            headers.ApplyTo(req, token, vision);

            try
            {
                // Same first-byte inactivity budget as PostMessagesAsync, applied per
                // attempt via the shared helper. A first-byte timeout throws a terminal
                // UpstreamTimeoutException (not transient), so the retry `when` below
                // does not catch it — the Codex endpoint maps it to a 504.
                return await SendWithFirstByteBudgetAsync(req, ct);
            }
            catch (Exception ex) when (
                attempt < _retry.MaxRetries
                && !ct.IsCancellationRequested
                && TransientUpstreamError.Is(ex))
            {
                req.Dispose();
                attempt++;
                var delayMs = ComputeBackoffMs(attempt);
                log.LogWarning(
                    "upstream POST /responses transient failure ({Type}: {Message}); "
                    + "retry {Attempt}/{Max} in {DelayMs}ms",
                    ex.GetType().Name, ex.Message, attempt, _retry.MaxRetries, delayMs);
                await Task.Delay(delayMs, ct);
            }
        }
    }

    private async ValueTask<(string Token, string BaseUrl)> ResolveAuthAsync(CancellationToken ct)
    {
        var token = await auth.GetCopilotTokenAsync(ct);
        var baseUrl = auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException(
                "Copilot API base URL is unknown — GetCopilotTokenAsync should have populated it.");
        return (token, baseUrl);
    }
}

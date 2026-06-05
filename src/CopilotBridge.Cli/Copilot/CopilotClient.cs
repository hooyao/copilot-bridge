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
    ILogger<CopilotClient> log) : ICopilotClient
{
    private readonly UpstreamRetryOptions _retry = retryOptions.Value;

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
                return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private async ValueTask<(string Token, string BaseUrl)> ResolveAuthAsync(CancellationToken ct)
    {
        var token = await auth.GetCopilotTokenAsync(ct);
        var baseUrl = auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException(
                "Copilot API base URL is unknown — GetCopilotTokenAsync should have populated it.");
        return (token, baseUrl);
    }
}

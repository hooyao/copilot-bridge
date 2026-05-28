using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Copilot;

namespace CopilotBridge.Cli.Copilot;

internal sealed class CopilotClient(
    HttpClient http,
    IAuthService auth,
    CopilotHeaderFactory headers) : ICopilotClient
{
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
        CancellationToken ct = default)
    {
        var (token, baseUrl) = await ResolveAuthAsync(ct);

        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = new ReadOnlyMemoryContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        headers.ApplyTo(req, token, vision);
        if (anthropicBeta is { Count: > 0 })
        {
            req.Headers.Add("anthropic-beta", string.Join(',', anthropicBeta));
        }

        // ResponseHeadersRead so the caller can stream the SSE body without
        // buffering the whole response. The caller owns disposal of the result.
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

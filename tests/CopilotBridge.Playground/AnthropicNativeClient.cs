using System.Net;
using System.Text;

namespace CopilotBridge.Playground;

/// <summary>
/// Minimal direct client for <c>api.anthropic.com</c>'s native messages API.
/// Mirrors <see cref="PlaygroundClient"/>'s shape so comparison tests can hit
/// both Copilot and Anthropic with identical JSON bodies and diff the responses.
/// Key comes from <see cref="LocalConfig.AnthropicApiKey"/> — the constructor
/// throws when it's missing so the calling test can skip cleanly.
/// </summary>
internal sealed class AnthropicNativeClient : IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public AnthropicNativeClient()
    {
        _apiKey = LocalConfig.AnthropicApiKey
            ?? throw new InvalidOperationException("AnthropicApiKey not set in appsettings.local.json — comparison tests cannot run.");
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge-playground/0.1");
    }

    /// <summary>
    /// POST <c>/v1/messages</c> to Anthropic native. Returns status + body without
    /// throwing so the test can observe rate-limit errors and skip rather than fail.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> TryPostMessagesAsync(
        string jsonBody,
        IReadOnlyList<string>? anthropicBeta = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages");
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        if (anthropicBeta is { Count: > 0 })
            req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(',', anthropicBeta));
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.StatusCode, body);
    }

    public void Dispose() => _http.Dispose();
}

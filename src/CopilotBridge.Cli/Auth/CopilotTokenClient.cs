using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Exchanges a GitHub OAuth token for a short-lived Copilot bearer token via
/// <c>GET /copilot_internal/v2/token</c>. Implementation detail of <see cref="AuthService"/>.
/// </summary>
internal sealed class CopilotTokenClient(HttpClient http)
{
    private const string TokenUrl = "https://api.github.com/copilot_internal/v2/token";

    public async ValueTask<CopilotTokenResponse> FetchAsync(string githubToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", githubToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Failed to fetch Copilot token: {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}");
        }

        return await resp.Content.ReadFromJsonAsync(JsonContext.Default.CopilotTokenResponse, ct)
               ?? throw new InvalidOperationException("Empty Copilot token response.");
    }
}

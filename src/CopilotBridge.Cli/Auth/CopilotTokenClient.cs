using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Exchanges a GitHub OAuth token for a short-lived Copilot bearer token via
/// <c>GET /copilot_internal/v2/token</c>. Implementation detail of <see cref="AuthService"/>.
/// </summary>
/// <remarks>
/// Stateless: the caller creates an <see cref="HttpClient"/> at its own call site
/// and passes it in, so nothing here holds one.
/// </remarks>
internal static class CopilotTokenClient
{
    private const string TokenUrl = "https://api.github.com/copilot_internal/v2/token";

    public static async ValueTask<CopilotTokenResponse> FetchAsync(
        HttpClient http, string githubToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", githubToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2025-04-01");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GitHubApiRequestException("Copilot token exchange", resp.StatusCode);

        return await resp.Content.ReadFromJsonAsync(JsonContext.Default.CopilotTokenResponse, ct)
               ?? throw new InvalidOperationException("Empty Copilot token response.");
    }
}

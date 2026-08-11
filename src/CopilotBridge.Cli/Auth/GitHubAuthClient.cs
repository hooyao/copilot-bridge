using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// The GitHub OAuth device-code endpoints. Implementation detail of
/// <see cref="AuthService"/>; should not be used directly outside the Auth folder.
/// </summary>
/// <remarks>
/// Stateless: the caller creates an <see cref="HttpClient"/> at its own call site
/// and passes it in, so nothing here holds one (which would pin a pooled handler
/// and defeat the factory's rotation).
/// </remarks>
internal static class GitHubAuthClient
{
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    // Official GitHub Copilot OAuth client id (same one VS Code Copilot uses).
    public const string ClientId = "Iv1.b507a08c87ecfe98";
    public const string Scope = "read:user";

    public static async ValueTask<DeviceCodeResponse> RequestDeviceCodeAsync(
        HttpClient http, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = JsonContent.Create(
                new DeviceCodeRequest(ClientId, Scope),
                JsonContext.Default.DeviceCodeRequest),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(JsonContext.Default.DeviceCodeResponse, ct))
               ?? throw new InvalidOperationException("Empty device-code response from GitHub.");
    }

    public static async ValueTask<AccessTokenResponse> PollAccessTokenAsync(
        HttpClient http,
        DeviceCodeResponse deviceCode,
        CancellationToken ct = default)
    {
        var pollDelay = TimeSpan.FromSeconds(deviceCode.Interval + 1);
        var body = new AccessTokenRequest(
            ClientId,
            deviceCode.DeviceCode,
            "urn:ietf:params:oauth:grant-type:device_code");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollDelay, ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
            {
                Content = JsonContent.Create(body, JsonContext.Default.AccessTokenRequest),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new GitHubOAuthException(
                    "device-token exchange", errorCode: null, resp.StatusCode);

            var result = await resp.Content.ReadFromJsonAsync(JsonContext.Default.AccessTokenResponse, ct);
            if (result?.AccessToken is { Length: > 0 }) return result;

            switch (result?.Error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    pollDelay += TimeSpan.FromSeconds(5);
                    break;
                case "expired_token":
                    throw new GitHubOAuthException("device-token exchange", result.Error);
                case "access_denied":
                    throw new GitHubOAuthException("device-token exchange", result.Error);
                case { Length: > 0 } error:
                    throw new GitHubOAuthException("device-token exchange", error);
            }
        }
    }

    public static async ValueTask<AccessTokenResponse> RefreshAccessTokenAsync(
        HttpClient http,
        string refreshToken,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
        {
            Content = JsonContent.Create(
                new RefreshTokenRequest
                {
                    ClientId = ClientId,
                    RefreshToken = refreshToken,
                },
                JsonContext.Default.RefreshTokenRequest),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GitHubOAuthException("refresh-token exchange", null, resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync(
            JsonContext.Default.AccessTokenResponse, ct);
        if (result?.AccessToken is { Length: > 0 }) return result;

        throw new GitHubOAuthException(
            "refresh-token exchange",
            result?.Error ?? "missing_access_token");
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Thin wrapper around the GitHub OAuth device-code endpoints. Implementation detail of
/// <see cref="AuthService"/>; should not be used directly outside the Auth folder.
/// </summary>
internal sealed class GitHubAuthClient(HttpClient http)
{
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    // Official GitHub Copilot OAuth client id (same one VS Code Copilot uses).
    public const string ClientId = "Iv1.b507a08c87ecfe98";
    public const string Scope = "read:user";

    public async ValueTask<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct = default)
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

    public async ValueTask<string> PollAccessTokenAsync(
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
            if (!resp.IsSuccessStatusCode) continue;

            var result = await resp.Content.ReadFromJsonAsync(JsonContext.Default.AccessTokenResponse, ct);
            if (result?.AccessToken is { Length: > 0 } token) return token;

            switch (result?.Error)
            {
                case "slow_down":
                    pollDelay += TimeSpan.FromSeconds(5);
                    break;
                case "expired_token":
                    throw new InvalidOperationException("Device code expired. Run `auth login` again.");
                case "access_denied":
                    throw new InvalidOperationException("Authorization was denied.");
            }
        }
    }
}

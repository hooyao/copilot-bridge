using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// The GitHub OAuth device-code endpoints. Implementation detail of
/// <see cref="CredentialService"/>; should not be used directly outside the Auth folder.
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

    // Fresh logins mirror the official GitHub Copilot Plugin device flow. The
    // public client id is persisted with version-3 credentials and no client
    // secret is used by RFC 8628 device authorization.
    public const string ClientId = GitHubOAuthProvider.CopilotPluginClientId;
    public const string Scope = GitHubOAuthProvider.CopilotPluginScope;

    public static async ValueTask<DeviceCodeResponse> RequestDeviceCodeAsync(
        HttpClient http, CancellationToken ct = default) =>
        await RequestDeviceCodeAsync(
            http, GitHubOAuthLoginProvider.OfficialCopilotPlugin, ct).ConfigureAwait(false);

    public static async ValueTask<DeviceCodeResponse> RequestDeviceCodeAsync(
        HttpClient http,
        GitHubOAuthLoginProvider provider,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = Form([
                new("client_id", provider.ClientId),
                new("scope", provider.Scope),
            ]),
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
        CancellationToken ct = default) =>
        await PollAccessTokenAsync(
            http, deviceCode, GitHubOAuthLoginProvider.OfficialCopilotPlugin, ct)
            .ConfigureAwait(false);

    public static async ValueTask<AccessTokenResponse> PollAccessTokenAsync(
        HttpClient http,
        DeviceCodeResponse deviceCode,
        GitHubOAuthLoginProvider provider,
        CancellationToken ct = default)
    {
        var pollDelay = TimeSpan.FromSeconds(deviceCode.Interval + 1);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollDelay, ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
            {
                Content = Form([
                    new("client_id", provider.ClientId),
                    new("device_code", deviceCode.DeviceCode),
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                ]),
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
        CancellationToken ct = default) =>
        await RefreshAccessTokenAsync(
            http,
            refreshToken,
            GitHubOAuthProvider.CopilotPluginClientId,
            ct).ConfigureAwait(false);

    public static async ValueTask<AccessTokenResponse> RefreshAccessTokenAsync(
        HttpClient http,
        string refreshToken,
        string clientId,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
        {
            Content = JsonContent.Create(
                new RefreshTokenRequest
                {
                    ClientId = clientId,
                    RefreshToken = refreshToken,
                },
                JsonContext.Default.RefreshTokenRequest),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode && IsTransientRefreshStatus(resp.StatusCode))
            throw new GitHubApiRequestException("refresh-token exchange", resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync(
            JsonContext.Default.AccessTokenResponse, ct);
        if (resp.IsSuccessStatusCode && result?.AccessToken is { Length: > 0 })
            return result;

        if (IsRefreshCredentialRejection(result?.Error))
            throw new GitHubRefreshCredentialRejectedException(
                result!.Error,
                resp.IsSuccessStatusCode ? null : resp.StatusCode);

        throw new GitHubOAuthException(
            "refresh-token exchange",
            result?.Error ?? "missing_access_token",
            resp.IsSuccessStatusCode ? null : resp.StatusCode);
    }

    private static bool IsTransientRefreshStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static bool IsRefreshCredentialRejection(string? errorCode) =>
        errorCode is "bad_refresh_token"
            or "invalid_grant"
            or "expired_token"
            or "revoked_token";

    private static FormUrlEncodedContent Form(
        IEnumerable<KeyValuePair<string, string>> fields) => new(fields);
}

using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.GitHub;

internal sealed record RefreshTokenRequest
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("grant_type")]
    public string GrantType { get; init; } = "refresh_token";

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    public override string ToString() =>
        $"RefreshTokenRequest {{ ClientId = {ClientId}, GrantType = {GrantType}, "
        + "RefreshToken = (redacted) }";
}

namespace CopilotBridge.Cli.Models.GitHub;

internal sealed record AccessTokenRequest(string ClientId, string DeviceCode, string GrantType);

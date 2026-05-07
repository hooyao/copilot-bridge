namespace CopilotBridge.Cli.Models.GitHub;

internal sealed record AccessTokenResponse
{
    public string? AccessToken { get; init; }
    public string? TokenType { get; init; }
    public string? Scope { get; init; }
    public string? Error { get; init; }
    public string? ErrorDescription { get; init; }
    public string? ErrorUri { get; init; }
}

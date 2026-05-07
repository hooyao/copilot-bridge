namespace CopilotBridge.Cli.Models.GitHub;

internal sealed record GitHubUser
{
    public required string Login { get; init; }
    public long Id { get; init; }
}

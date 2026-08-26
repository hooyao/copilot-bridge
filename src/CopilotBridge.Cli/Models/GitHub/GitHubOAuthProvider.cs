namespace CopilotBridge.Cli.Models.GitHub;

/// <summary>
/// OAuth client identities used by the supported credential versions.
/// </summary>
internal static class GitHubOAuthProvider
{
    // GitHub CLI source: internal/authflow/flow.go at
    // a255baf71d13fe5947a4eb7ad521ffd412d64cee (2026-08-20).
    public const string GitHubCliClientId = "178c6fc778ccc68e1d6a";
    public const string GitHubCliScope = "repo read:org gist";

    // Official GitHub Copilot OAuth client used by older bridge credentials.
    public const string CopilotPluginClientId = "Iv1.b507a08c87ecfe98";
    public const string CopilotPluginScope = "read:user";
}

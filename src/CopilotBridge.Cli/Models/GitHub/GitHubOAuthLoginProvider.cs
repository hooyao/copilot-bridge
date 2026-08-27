namespace CopilotBridge.Cli.Models.GitHub;

/// <summary>
/// Immutable provider selected for one interactive OAuth login. Persisted
/// credentials keep their own recorded issuer; changing config affects only a
/// later login.
/// </summary>
internal sealed record GitHubOAuthLoginProvider(
    string ClientId,
    string Scope,
    int CredentialVersion,
    bool IsDirect)
{
    public static GitHubOAuthLoginProvider OfficialCopilotPlugin { get; } = new(
        GitHubOAuthProvider.CopilotPluginClientId,
        GitHubOAuthProvider.CopilotPluginScope,
        CredentialFileRecord.CopilotPluginExplicitProviderVersion,
        IsDirect: false);

    public static GitHubOAuthLoginProvider Custom(string clientId) => new(
        clientId,
        GitHubOAuthProvider.CopilotPluginScope,
        CredentialFileRecord.CustomOAuthDirectVersion,
        IsDirect: true);
}

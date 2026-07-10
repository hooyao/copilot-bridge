namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Resolves the GitHub credential used to obtain a short-lived Copilot token.
/// An explicit environment credential is ephemeral and takes precedence over
/// the encrypted token persisted by the interactive device-code flow.
/// </summary>
internal static class GitHubTokenSource
{
    public const string EnvironmentVariableName = "COPILOT_BRIDGE_GITHUB_TOKEN";

    public static string? TryLoad() => Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariableName),
        TokenStore.TryLoad());

    public static bool UsesEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariableName));

    public static string Location => UsesEnvironment
        ? $"environment variable {EnvironmentVariableName}"
        : TokenStore.FilePath;

    internal static string? Resolve(string? environmentToken, string? persistedToken) =>
        !string.IsNullOrWhiteSpace(environmentToken) ? environmentToken : persistedToken;
}

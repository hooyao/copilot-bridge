namespace CopilotBridge.Update.Wire;

/// <summary>
/// The one-launch-only context an updater passes to a bridge it launches, via
/// child-only environment variables (never persisted configuration, never a
/// shell command line). Its presence both (a) suppresses the startup update
/// check for that single launch — so a fresh install or a rollback cannot
/// immediately re-check and loop — and (b) tells the bridge to report Ready to
/// the updater once it truly reaches serving state. A launch WITHOUT this
/// context is an ordinary launch and does nothing update-related.
/// </summary>
internal sealed record UpdateLaunchContext(
    string AttemptId,
    string Role,
    string PipeName,
    string Token,
    string ExpectedVersion)
{
    // Child-only environment variable names. Chosen with a distinctive prefix so
    // they can't collide with anything an operator sets intentionally.
    public const string EnvAttempt = "COPILOT_BRIDGE_UPDATE_ATTEMPT";
    public const string EnvRole = "COPILOT_BRIDGE_UPDATE_ROLE";
    public const string EnvPipe = "COPILOT_BRIDGE_UPDATE_PIPE";
    public const string EnvToken = "COPILOT_BRIDGE_UPDATE_TOKEN";
    public const string EnvVersion = "COPILOT_BRIDGE_UPDATE_VERSION";

    /// <summary>
    /// Parse the context from the current process environment. Returns null when
    /// absent or incomplete/malformed — a malformed context is never persisted or
    /// partially honored; the launch just behaves as an ordinary startup.
    /// </summary>
    public static UpdateLaunchContext? FromEnvironment(Func<string, string?> getEnv)
    {
        var attempt = getEnv(EnvAttempt);
        var role = getEnv(EnvRole);
        var pipe = getEnv(EnvPipe);
        var token = getEnv(EnvToken);
        var version = getEnv(EnvVersion);

        if (string.IsNullOrEmpty(attempt)
            || string.IsNullOrEmpty(pipe)
            || string.IsNullOrEmpty(token)
            || string.IsNullOrEmpty(version))
        {
            return null;
        }

        // Only the two defined roles are accepted; anything else is malformed.
        if (role is not (UpdateWire.RoleTarget or UpdateWire.RoleRollback))
        {
            return null;
        }

        return new UpdateLaunchContext(attempt, role, pipe, token, version);
    }

    /// <summary>Apply this context to a child process's environment dictionary.</summary>
    public void ApplyTo(IDictionary<string, string?> childEnvironment)
    {
        childEnvironment[EnvAttempt] = AttemptId;
        childEnvironment[EnvRole] = Role;
        childEnvironment[EnvPipe] = PipeName;
        childEnvironment[EnvToken] = Token;
        childEnvironment[EnvVersion] = ExpectedVersion;
    }
}

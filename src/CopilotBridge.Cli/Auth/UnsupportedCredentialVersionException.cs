namespace CopilotBridge.Cli.Auth;

internal sealed class UnsupportedCredentialVersionException(int version)
    : InvalidOperationException(
        $"Unsupported credential version {version} in github_credentials.dat. "
        + "Use a bridge version that understands this file; it was left unchanged.")
{
    public int Version { get; } = version;
}

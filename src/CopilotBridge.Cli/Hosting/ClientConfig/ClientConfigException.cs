namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Raised by a configurator's planning/merge path when it refuses to proceed —
/// most importantly when an existing config file cannot be safely merged (non-empty
/// but unparseable), where overwriting would silently discard the user's unrelated
/// content. The dispatcher (<see cref="ConfigCommand"/>) catches this and aborts the
/// command without writing, surfacing the message to the user.
/// </summary>
internal sealed class ClientConfigException : System.Exception
{
    public ClientConfigException(string message) : base(message)
    {
    }
}

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Recognizable startup-time error — caught at the top of <c>Program.cs</c>
/// and surfaced to the operator via <see cref="FatalErrorHandler.PauseAndExit"/>
/// without a stack trace. Use for "configuration is invalid in a way the user
/// must fix" cases (bad <c>--port</c> value, missing required setting, etc.).
/// Genuine bugs / unexpected exceptions bubble up unwrapped so the trace is
/// preserved.
/// </summary>
internal sealed class BridgeStartupException : Exception
{
    public BridgeStartupException(string message) : base(message) { }
    public BridgeStartupException(string message, Exception inner) : base(message, inner) { }
}

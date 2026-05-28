namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Mutable static handoff slot for the single <see cref="BridgeIoSink"/>
/// instance shared between two consumers that are constructed at different
/// times: <c>Program.cs</c>'s Serilog configuration (which needs the sink
/// to plug into its pipeline) and <c>BridgeHost.ConfigureServices</c> (which
/// registers the same instance as a DI singleton so it can be disposed
/// deterministically alongside the rest of the host on shutdown).
/// </summary>
/// <remarks>
/// Why a static holder rather than constructor-inject the sink into Serilog:
/// <c>Log.Logger</c> is a process-wide static, and the slim host's DI
/// container does not exist yet when <c>Program.cs</c> wires up Serilog.
/// A regular DI singleton in the host would create a second instance,
/// orphaning the one Serilog already holds.
/// </remarks>
internal static class BridgeIoSinkHolder
{
    public static BridgeIoSink? Instance { get; set; }
}

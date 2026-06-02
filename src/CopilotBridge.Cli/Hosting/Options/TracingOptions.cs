namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Tracing</c>. Controls the
/// per-request audit JSON pipeline (<see cref="Logging.BridgeIoSink"/>):
/// when <see cref="Enabled"/> is true, four JSON artifacts per request land
/// under <see cref="Directory"/>. The audit captures full request/response
/// bodies (including user prompts), so it is OFF by default — turn on only
/// to debug a specific issue, then turn off again.
/// </summary>
internal sealed class TracingOptions
{
    public bool Enabled { get; set; }

    /// <summary>Path relative to the .exe unless absolute. Default: <c>request-traces</c>.</summary>
    public string Directory { get; set; } = "request-traces";
}

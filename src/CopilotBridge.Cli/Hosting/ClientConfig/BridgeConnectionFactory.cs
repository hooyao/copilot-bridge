using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Computes a <see cref="BridgeConnection"/> from the loaded configuration — the
/// single seam that reads <c>appsettings.json</c> the same way the server does, so
/// what <c>config</c> writes into a client matches how <c>serve</c> actually runs.
/// </summary>
/// <remarks>
/// Binds the SAME strongly-typed options sections the server binds in
/// <c>AddBridgeServer</c> (<see cref="BridgeServerOptions"/>,
/// <see cref="ResponseLeakGuardOptions"/>, <see cref="ToolInputValidationOptions"/>,
/// <see cref="RunawayGuardOptions"/>) rather than reading raw
/// <c>GetValue&lt;&gt;("Pipeline:Detectors:…")</c> strings — so the two code paths
/// cannot disagree on key names or defaults. Binding uses the source-generated
/// configuration binder (<c>EnableConfigurationBindingGenerator</c>), so it is
/// AOT-clean.
/// </remarks>
internal static class BridgeConnectionFactory
{
    /// <summary>
    /// Derive the connection facts. <paramref name="cliPort"/> (the <c>--port</c>
    /// override) wins over <c>Server:Port</c>; a missing <c>Server</c> section falls
    /// back to the <see cref="BridgeServerOptions"/> default (8765).
    /// </summary>
    public static BridgeConnection Create(IConfiguration config, int? cliPort = null)
    {
        var server = config.GetSection("Server").Get<BridgeServerOptions>() ?? new BridgeServerOptions();
        var leak = config.GetSection("Pipeline:Detectors:ResponseLeakGuard").Get<ResponseLeakGuardOptions>()
            ?? new ResponseLeakGuardOptions();
        var toolInput = config.GetSection("Pipeline:Detectors:ToolInputValidation").Get<ToolInputValidationOptions>()
            ?? new ToolInputValidationOptions();
        var runaway = config.GetSection("Pipeline:Detectors:RunawayGuard").Get<RunawayGuardOptions>()
            ?? new RunawayGuardOptions();

        var port = cliPort ?? server.Port;

        // The fallback env must be written whenever ANY response detector can inject an
        // error mid-stream — that is the exact condition under which Claude Code's silent
        // non-streaming re-request would otherwise swallow the abort instead of retrying
        // the whole turn. ResponseLeakGuard and ToolInputValidation are mid-stream only
        // when PreserveStream is set; RunawayGuard has no PreserveStream toggle — it
        // always aborts mid-stream when enabled, so its Enabled flag alone counts.
        var needFallback =
            (leak.Enabled && leak.PreserveStream) ||
            (toolInput.Enabled && toolInput.PreserveStream) ||
            runaway.Enabled;

        return new BridgeConnection(port, needFallback);
    }
}

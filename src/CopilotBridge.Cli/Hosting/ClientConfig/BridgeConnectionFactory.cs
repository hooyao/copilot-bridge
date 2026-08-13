using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Computes a <see cref="BridgeConnection"/> from the loaded configuration — the
/// single seam that reads <c>appsettings.json</c> the same way the server does, so
/// what <c>config</c> writes into a client matches how <c>serve</c> actually runs.
/// </summary>
/// <remarks>
/// Deliberately binds only <see cref="BridgeServerOptions"/>. Timeout, retry,
/// detector, telemetry, 1M, and fallback policy are not connection facts and can
/// never flow from appsettings into a client configurator through this type.
/// Binding uses the source-generated configuration binder and remains AOT-clean.
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
        var port = cliPort ?? server.Port;
        return new BridgeConnection(port);
    }
}

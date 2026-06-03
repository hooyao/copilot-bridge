namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Server</c> via the standard
/// source-generated <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>
/// pipeline. The <c>--port</c> CLI argument overrides <see cref="Port"/> via
/// <see cref="Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.PostConfigure{TOptions}"/>
/// so the precedence is: CLI &gt; appsettings &gt; built-in default (8765).
/// </summary>
internal sealed class BridgeServerOptions
{
    /// <summary>TCP port Kestrel listens on. 1-65535.</summary>
    public int Port { get; set; } = 8765;
}

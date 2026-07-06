using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Configuration-side wiring for the bridge — kept apart from
/// <see cref="BridgeServiceCollectionExtensions"/> because it has to run
/// against <c>builder.Configuration</c> (an <see cref="IConfigurationBuilder"/>),
/// not the service collection. Loads <c>appsettings.json</c> from the .exe's
/// directory; a missing file is a fatal startup error surfaced through
/// <see cref="FatalErrorHandler"/>.
/// </summary>
internal static class BridgeConfigurationExtensions
{
    /// <summary>
    /// Add the standard configuration sources to a <see cref="WebApplicationBuilder"/>:
    /// <c>appsettings.json</c> (required) is rebased to <see cref="AppContext.BaseDirectory"/>
    /// so the file is found next to the published .exe rather than in the
    /// process's <see cref="Environment.CurrentDirectory"/>.
    /// </summary>
    public static WebApplicationBuilder AddBridgeConfiguration(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddBridgeAppSettings();
        return builder;
    }

    /// <summary>
    /// The web-host-neutral core: add <c>appsettings.json</c> (required, rebased to
    /// <see cref="AppContext.BaseDirectory"/>, no reload-on-change) to any
    /// <see cref="IConfigurationBuilder"/>. Shared by <c>serve</c> (through
    /// <see cref="AddBridgeConfiguration"/>) and the <c>config</c> command's own
    /// composition root, so both read the exact same file the same way — no web
    /// host required to load settings.
    /// </summary>
    public static IConfigurationBuilder AddBridgeAppSettings(this IConfigurationBuilder configuration)
    {
        configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        return configuration;
    }
}

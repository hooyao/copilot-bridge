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
    /// <summary>The stock configuration file name, loaded from the .exe directory.</summary>
    public const string ConfigFileName = "appsettings.json";

    // Host-configuration switch the .NET Generic Host reads while bootstrapping to
    // decide whether the default appsettings JSON sources (and user-secrets) are
    // registered with reloadOnChange. Defaults to true. Passed as a command-line
    // argument through WebApplicationOptions.Args so it lands in the host's own
    // configuration — the supported, code-scoped way to set it (no process-wide
    // environment mutation). See dotnet/runtime
    // HostingHostBuilderExtensions.GetReloadConfigOnChangeValue, which reads
    // "hostBuilder:reloadConfigOnChange".
    private const string DisableConfigReloadArg = "--hostBuilder:reloadConfigOnChange=false";

    /// <summary>
    /// The host-builder arguments the <c>serve</c> host must be constructed with, so
    /// its default configuration sources do NOT watch the filesystem for changes.
    /// <para>
    /// <c>WebApplication.CreateSlimBuilder</c> otherwise registers
    /// <c>appsettings.json</c> / <c>appsettings.{Env}.json</c> / user-secrets with
    /// <c>reloadOnChange:true</c>, rooted at the content root (the process's current
    /// working directory). Because
    /// <see cref="Microsoft.Extensions.Configuration.ConfigurationManager"/> builds
    /// each provider eagerly as its source is added, a
    /// <see cref="Microsoft.Extensions.Configuration.FileConfigurationProvider"/>
    /// starts a <em>recursive</em> <c>FileSystemWatcher</c> (subdirectories included)
    /// over that whole subtree the instant the builder is constructed — alive for the
    /// entire process, independent of any request.
    /// </para>
    /// <para>
    /// On macOS that recursive watch walks into every File Provider domain under the
    /// working directory (iCloud Drive, Google Drive, …), which makes the OS raise a
    /// TCC access prompt for each cloud provider even when the bridge is completely
    /// idle with no client connected. The bridge never wants config hot-reload anyway
    /// — every options type is read once at startup and documents "restart to change",
    /// and our own <see cref="AddBridgeAppSettings"/> source already uses
    /// <c>reloadOnChange:false</c> — so we disable the default watcher at its source.
    /// </para>
    /// <para>
    /// The switch has to be supplied at construction (via
    /// <see cref="WebApplicationOptions.Args"/>), not afterwards: the host reads it
    /// while bootstrapping, and the watcher is created eagerly inside the builder, so
    /// mutating <c>builder.Configuration.Sources</c> afterwards is too late — the
    /// watch (and the prompt) has already fired. One switch disables watching for all
    /// the default JSON sources and user-secrets at once.
    /// </para>
    /// </summary>
    public static string[] DisableConfigFileWatchingArgs { get; } = [DisableConfigReloadArg];

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
            .AddJsonFile(ConfigFileName, optional: false, reloadOnChange: false);
        return configuration;
    }
}

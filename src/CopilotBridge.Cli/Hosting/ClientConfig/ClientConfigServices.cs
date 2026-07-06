using CopilotBridge.Cli.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// The dedicated, web-host-free composition root for the <c>config</c> command.
/// Registers ONLY what client auto-configuration needs — the loaded
/// <see cref="IConfiguration"/> and the set of <see cref="IClientConfigurator"/>
/// implementations. It never calls <c>AddBridgeServer</c>, constructs no
/// <c>WebApplication</c>, starts no Kestrel listener, and runs no hosted service, so
/// the config command's dependency graph is structurally disjoint from the proxy
/// server's startup path.
/// </summary>
/// <remarks>
/// Adding a new client is one <c>AddSingleton&lt;IClientConfigurator, …&gt;</c> line
/// here plus the new configurator class — no change to the dispatcher or the server.
/// </remarks>
internal static class ClientConfigServices
{
    /// <summary>
    /// Build a service provider containing the configuration and the client
    /// configurators. Loads <c>appsettings.json</c> via the same web-host-neutral
    /// helper the server uses (<see cref="BridgeConfigurationExtensions.AddBridgeAppSettings"/>).
    /// </summary>
    public static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .AddBridgeAppSettings()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        AddClientConfiguration(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Register the client configurators. The single place a new supported client is
    /// wired in.
    /// </summary>
    public static IServiceCollection AddClientConfiguration(this IServiceCollection services)
    {
        services.AddSingleton<IClientConfigurator, ClaudeCodeConfigurator>();
        services.AddSingleton<IClientConfigurator, CodexConfigurator>();
        return services;
    }
}

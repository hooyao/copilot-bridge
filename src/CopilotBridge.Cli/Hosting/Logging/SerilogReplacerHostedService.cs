using Microsoft.Extensions.Hosting;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Swaps the bootstrap Serilog logger for the production one (console + rolling
/// file + audit sink) while the host constructs its ordered hosted-service list.
/// Generic Host constructs every hosted service before it invokes any
/// <see cref="IHostedService.StartAsync"/> method, so doing the swap in this
/// service's own StartAsync would be too late: loggers injected into later
/// hosted services would already target the disposed bootstrap instance.
/// Registering this service first makes its constructor the ordering barrier.
/// </summary>
internal sealed class SerilogReplacerHostedService : IHostedService
{
    public SerilogReplacerHostedService(IServiceProvider services)
    {
        SerilogBootstrapper.ReplaceWithFull(services);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // BridgeIoSink and Log.CloseAndFlush are wired via the DI container's
        // disposal of singletons + the ProcessExit hook in Program.cs.
        return Task.CompletedTask;
    }
}

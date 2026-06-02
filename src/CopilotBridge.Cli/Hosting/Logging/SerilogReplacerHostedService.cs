using Microsoft.Extensions.Hosting;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Runs immediately after the host starts and swaps the bootstrap Serilog
/// logger for the production one (console + rolling file + audit sink). Has
/// to run before <see cref="BridgeStartupHostedService"/> so the auth flow
/// and startup banner are captured in the full pipeline; achieved by
/// registering this service first in
/// <see cref="BridgeServiceCollectionExtensions.AddBridgeServer"/>.
/// </summary>
internal sealed class SerilogReplacerHostedService : IHostedService
{
    private readonly IServiceProvider _services;

    public SerilogReplacerHostedService(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SerilogBootstrapper.ReplaceWithFull(_services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // BridgeIoSink and Log.CloseAndFlush are wired via the DI container's
        // disposal of singletons + the ProcessExit hook in Program.cs.
        return Task.CompletedTask;
    }
}

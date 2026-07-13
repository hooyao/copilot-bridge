using CopilotBridge.Update.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CopilotBridge.Cli.Update;

/// <summary>
/// Inert on ordinary launches. When the bridge was launched by an updater with a
/// valid one-launch <see cref="UpdateLaunchContext"/>, this hosted service
/// registers for <see cref="IHostApplicationLifetime.ApplicationStarted"/> and,
/// only then — after route/config validation, auth setup, all hosted-service
/// startup, and Kestrel listener start have completed — sends a single
/// role-scoped Ready message back to the updater over the named pipe. This is the
/// bridge's transactional proof of "actually serving"; the startup banner's
/// "listening" log is produced too early to serve as that proof.
/// </summary>
internal sealed class UpdateReadinessReporter : IHostedService
{
    private readonly UpdateLaunchContext? _context;
    private readonly IServiceProvider _services;

    public UpdateReadinessReporter(IServiceProvider services)
        : this(services, UpdateLaunchContext.FromEnvironment(Environment.GetEnvironmentVariable))
    {
    }

    // Test seam.
    internal UpdateReadinessReporter(IServiceProvider services, UpdateLaunchContext? context)
    {
        _services = services;
        _context = context;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_context is null)
        {
            return Task.CompletedTask; // ordinary launch: do nothing
        }

        // Resolve the lifetime lazily: the full generic host provides it, but the
        // bare AddBridgeServer container used by DI-graph validation tests does
        // not, and this service is inert there anyway.
        var lifetime = _services.GetService<IHostApplicationLifetime>();
        lifetime?.ApplicationStarted.Register(() =>
        {
            // Fire-and-forget: the updater is waiting on the pipe with its own
            // timeout, and a failure to report simply looks like "not ready".
            _ = ReportReadyAsync();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReportReadyAsync()
    {
        var ctx = _context!;
        var msg = new UpdateReadyMessage
        {
            AttemptId = ctx.AttemptId,
            Role = ctx.Role,
            Token = ctx.Token,
            Pid = Environment.ProcessId,
            Version = Hosting.ProductInfo.Version,
        };
        try
        {
            await UpdatePipeTransport.ClientSendLineAsync(
                ctx.PipeName,
                UpdatePipeCodec.EncodeReady(msg),
                TimeSpan.FromSeconds(10),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort. The updater's readiness timeout is the backstop.
        }
    }
}

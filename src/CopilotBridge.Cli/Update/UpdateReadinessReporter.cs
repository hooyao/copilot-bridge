using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Update.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotBridge.Cli.Update;

/// <summary>
/// Inert on ordinary launches. When the bridge was launched by an updater with a
/// valid one-launch <see cref="UpdateLaunchContext"/>, this hosted service
/// registers for <see cref="IHostApplicationLifetime.ApplicationStarted"/> and,
/// only then — after route/config validation, all hosted-service startup, and
/// Kestrel listener start have completed — sends a single
/// role-scoped Ready message back to the updater over the named pipe. This is the
/// bridge's transactional proof of "actually serving"; the startup banner's
/// "listening" log is produced too early to serve as that proof.
/// Authentication is deferred until after Ready is sent, then the ordinary
/// bootstrap resumes in the background. An external credential/network failure
/// is not evidence that the installed binary is unhealthy and never forces a
/// rollback loop.
/// </summary>
internal sealed class UpdateReadinessReporter : IHostedService
{
    private readonly UpdateLaunchContext? _context;
    private readonly IServiceProvider _services;
    private readonly Func<UpdateReadyMessage, CancellationToken, Task<bool>> _sendReady;

    public UpdateReadinessReporter(IServiceProvider services)
        : this(
            services,
            UpdateLaunchContext.FromEnvironment(Environment.GetEnvironmentVariable),
            sendReady: null)
    {
    }

    // Test seam.
    internal UpdateReadinessReporter(
        IServiceProvider services,
        UpdateLaunchContext? context,
        Func<UpdateReadyMessage, CancellationToken, Task<bool>>? sendReady = null)
    {
        _services = services;
        _context = context;
        _sendReady = sendReady ?? ((message, cancellationToken) =>
            UpdatePipeTransport.ClientSendLineAsync(
                context!.PipeName,
                UpdatePipeCodec.EncodeReady(message),
                TimeSpan.FromSeconds(10),
                cancellationToken));
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
        if (lifetime is null) return Task.CompletedTask;
        var stopping = lifetime.ApplicationStopping;
        lifetime.ApplicationStarted.Register(() =>
        {
            // Fire-and-forget: the updater is waiting on the pipe with its own
            // timeout. Authentication starts only after the send and remains
            // outside host startup/readiness, so it cannot trigger rollback.
            _ = ReportReadyAndResumeAuthenticationAsync(stopping);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task ReportReadyAndResumeAuthenticationAsync(CancellationToken cancellationToken)
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
            if (!await _sendReady(msg, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Best-effort. The updater's readiness timeout is the backstop, and
            // auth must not begin when Ready was not delivered.
            return;
        }

        var log = _services.GetService<ILogger<UpdateReadinessReporter>>()
            ?? NullLogger<UpdateReadinessReporter>.Instance;
        try
        {
            var auth = _services.GetRequiredService<IAuthService>();
            await BridgeStartupHostedService.BootstrapAuthenticationAsync(
                auth,
                updateContext: null,
                log,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown cancels the post-readiness device flow or refresh.
        }
        catch (Exception ex)
        {
            log.LogWarning(
                "Post-update authentication failed ({Error}); the bridge remains serving. Run `auth login` to authenticate.",
                ex.GetType().Name);
        }
    }

}

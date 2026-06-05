using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Production startup chores, scheduled by the generic host:
/// <list type="number">
///   <item>Validate the routing config (fail fast on misconfigured locations).</item>
///   <item>Ensure a GitHub OAuth token exists; run the device-code flow if not.</item>
///   <item>Fetch a Copilot bearer token (populates
///         <see cref="IAuthService.CopilotApiBaseUrl"/>).</item>
///   <item>Print the listening URL, upstream URL, trace directory, and route
///         counts so the operator can confirm the bridge is healthy.</item>
/// </list>
/// Throws on validation / auth failure; the generic host surfaces this to
/// <c>app.RunAsync</c> which bubbles up to <c>Program.cs</c>'s top-level
/// catch — <see cref="FatalErrorHandler"/> then displays the error and
/// pauses for keypress.
/// </summary>
internal sealed class BridgeStartupHostedService : IHostedService
{
    private readonly IAuthService _auth;
    private readonly IOptions<BridgeServerOptions> _server;
    private readonly IOptions<RoutesConfig> _routes;
    private readonly ModelProfileCatalog _catalog;
    private readonly ProductInfo _product;
    private readonly Logging.BridgeIoSink? _ioSink;
    private readonly ILogger<BridgeStartupHostedService> _log;

    public BridgeStartupHostedService(
        IAuthService auth,
        IOptions<BridgeServerOptions> server,
        IOptions<RoutesConfig> routes,
        ModelProfileCatalog catalog,
        ProductInfo product,
        ILogger<BridgeStartupHostedService> log,
        Logging.BridgeIoSink? ioSink = null)
    {
        _auth = auth;
        _server = server;
        _routes = routes;
        _catalog = catalog;
        _product = product;
        _log = log;
        _ioSink = ioSink;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. Routes config — invalid shape is a user-fixable mistake; surface
        //    as BridgeStartupException so FatalErrorHandler renders just the
        //    message (no noisy stack trace).
        try
        {
            RoutesValidator.Validate(_routes.Value);
        }
        catch (Exception ex) when (ex is not BridgeStartupException)
        {
            throw new BridgeStartupException($"Invalid Routing config: {ex.Message}", ex);
        }

        // 2-3. Auth: device-code flow if no token, then exchange for Copilot
        //      token. Cancellation propagates back out as OperationCanceledException
        //      (handled by host as a clean shutdown, not a fatal).
        if (!_auth.IsAuthenticated)
        {
            _log.LogInformation(
                "No GitHub token on disk — starting device-code flow. Complete the browser handshake to continue.");
        }
        try
        {
            await _auth.EnsureGitHubTokenAsync(cancellationToken).ConfigureAwait(false);
            await _auth.GetCopilotTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BridgeStartupException($"Auth setup failed: {ex.Message}", ex);
        }

        // 4. Operator-facing summary. ILogger so the formatting matches every
        //    other line in the rolling log.
        var port = _server.Value.Port;
        var textLogDir = Path.Combine(AppContext.BaseDirectory, "log");
        _log.LogInformation("{ProductName} v{ProductVersion} starting", _product.Name, _product.Version);
        _log.LogInformation("copilot-bridge listening on http://localhost:{Port}", port);
        _log.LogInformation("Upstream: {UpstreamUrl}", _auth.CopilotApiBaseUrl);
        _log.LogInformation("Text log: {LogDir} (one file per process start)", textLogDir);
        if (_ioSink is not null)
        {
            _log.LogInformation(
                "Req trace: {TraceDir} (enabled — per-request audit JSON written here)",
                _ioSink.Directory);
        }
        else
        {
            _log.LogInformation(
                "Req trace: disabled — set Tracing.Enabled=true in appsettings.json to capture per-request bodies");
        }
        _log.LogInformation(
            "Routes:    {LocCount} user locations; catalog: {ModelCount} model profiles",
            _routes.Value.Locations.Count, _catalog.Count);
        _log.LogInformation(
            "Model profile catalog loaded with {Count} profiles: {Ids}",
            _catalog.Count, string.Join(", ", _catalog.KnownIds));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

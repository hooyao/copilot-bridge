using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Update.Wire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Production startup chores, scheduled by the generic host:
/// <list type="number">
///   <item>Validate the routing config (fail fast on misconfigured locations).</item>
///   <item>On an ordinary launch, ensure a GitHub OAuth token exists and resolve
///         a direct or exchanged Copilot authentication lease. On an
///         updater-managed activation, defer all credential access until after
///         Ready so external auth cannot trigger rollback.</item>
///   <item>Print the listening URL, upstream URL, trace directory, and route
///         counts so the operator can confirm the bridge is healthy.</item>
/// </list>
/// Throws on validation / ordinary-launch auth failure; the generic host surfaces this to
/// <c>app.RunAsync</c> which bubbles up to <c>Program.cs</c>'s top-level
/// catch — <see cref="FatalErrorHandler"/> then displays the error and
/// pauses for keypress.
/// </summary>
internal sealed class BridgeStartupHostedService : IHostedService
{
    private readonly IAuthService _auth;
    private readonly IOptions<BridgeServerOptions> _server;
    private readonly IOptions<RoutesConfig> _routes;
    private readonly IOptions<UpstreamTimeoutOptions> _upstreamTimeout;
    private readonly IOptions<UpstreamRetryOptions> _upstreamRetry;
    private readonly IOptions<ResponseLeakGuardOptions> _leakGuard;
    private readonly IOptions<ToolInputValidationOptions> _toolInputValidation;
    private readonly ModelProfileCatalog _catalog;
    private readonly CodexModelProfileCatalog _codexCatalog;
    private readonly Logging.BridgeIoSink? _ioSink;
    private readonly ILogger<BridgeStartupHostedService> _log;

    public BridgeStartupHostedService(
        IAuthService auth,
        IOptions<BridgeServerOptions> server,
        IOptions<RoutesConfig> routes,
        IOptions<UpstreamTimeoutOptions> upstreamTimeout,
        IOptions<UpstreamRetryOptions> upstreamRetry,
        IOptions<ResponseLeakGuardOptions> leakGuard,
        IOptions<ToolInputValidationOptions> toolInputValidation,
        ModelProfileCatalog catalog,
        CodexModelProfileCatalog codexCatalog,
        ILogger<BridgeStartupHostedService> log,
        Logging.BridgeIoSink? ioSink = null)
    {
        _auth = auth;
        _server = server;
        _routes = routes;
        _upstreamTimeout = upstreamTimeout;
        _upstreamRetry = upstreamRetry;
        _leakGuard = leakGuard;
        _toolInputValidation = toolInputValidation;
        _catalog = catalog;
        _codexCatalog = codexCatalog;
        _log = log;
        _ioSink = ioSink;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("{ProductName} v{ProductVersion} starting", ProductInfo.Name, ProductInfo.Version);

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

        // 2-3. Auth: ordinary launches preserve the interactive startup contract.
        //      An updater-managed target or rollback launch must prove only that the
        //      installed program can validate local configuration and start serving.
        //      Credential migration and network refresh are external, recoverable
        //      operations; running either before Ready can turn a transient auth
        //      failure into an endless update/rollback loop (and can mutate a
        //      credential into a format the rollback binary cannot read).
        var updateContext = UpdateLaunchContext.FromEnvironment(
            Environment.GetEnvironmentVariable);
        var authenticationReady = await BootstrapAuthenticationAsync(
            _auth, updateContext, _log, cancellationToken).ConfigureAwait(false);

        // 4. Operator-facing summary. ILogger so the formatting matches every
        //    other line in the rolling log.
        var port = _server.Value.Port;
        var textLogDir = Path.Combine(AppContext.BaseDirectory, "log");
        _log.LogInformation("copilot-bridge listening on http://localhost:{Port}", port);
        if (authenticationReady)
        {
            _log.LogInformation("Upstream: {UpstreamUrl}", _auth.CopilotApiBaseUrl);
        }
        else
        {
            _log.LogInformation(
                "Upstream: authentication deferred until after update readiness; first-run login will resume automatically");
        }
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
            "Routes:    {LocCount} user locations; catalog: {ModelCount} Anthropic + {CodexCount} Codex model profiles",
            _routes.Value.Locations.Count, _catalog.Count, _codexCatalog.Count);
        _log.LogInformation(
            "Anthropic profiles ({Count}): {Ids}",
            _catalog.Count, string.Join(", ", _catalog.KnownIds));
        _log.LogInformation(
            "Codex profiles ({Count}): {Ids}",
            _codexCatalog.Count, string.Join(", ", _codexCatalog.KnownIds));

        // Per-phase timeout bounds from the bridge and the client's GLOBAL settings.
        // Best-effort: an unreadable client settings file must not fail startup.
        // PreserveStream=false drains the whole response before delivering it, so no
        // keepalive can reach the client mid-turn — the report must say so rather than
        // promise pings the configuration cannot deliver.
        string? testClaudeSettingsPath = null;
        string? testCodexConfigPath = null;
        string? testClaudeVersion = null;
#if DEBUG
        // Real-subprocess verification can isolate the two observed GLOBAL files
        // without changing the user's actual homes. Release builds expose no such
        // override and always inspect the native global locations.
        testClaudeSettingsPath = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_CLAUDE_SETTINGS_PATH");
        testCodexConfigPath = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_CODEX_CONFIG_PATH");
        testClaudeVersion = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_CLAUDE_VERSION");
#endif
        TimeoutBudgetReport.Emit(
            _upstreamTimeout.Value,
            _log,
            settingsPathOverride: testClaudeSettingsPath,
            wholeResponseBuffering: WholeResponseBufferingActive(
                _leakGuard.Value,
                _toolInputValidation.Value),
            codexConfigPathOverride: testCodexConfigPath,
            retryOptions: _upstreamRetry.Value,
            claudeVersionOverride: testClaudeVersion);
    }

    /// <summary>
    /// Mirrors the active detectors' <c>RequiresBuffering</c> contract so startup
    /// does not promise downstream keepalive on a path that drains the whole stream
    /// first. Keep this predicate contract-tested beside the detector options.
    /// </summary>
    internal static bool WholeResponseBufferingActive(
        ResponseLeakGuardOptions leak,
        ToolInputValidationOptions toolInput)
    {
        var leakBuffers = leak.Enabled && !leak.PreserveStream;
        var toolInputBuffers = toolInput.Enabled
            && !toolInput.PreserveStream
            && (toolInput.MalformedJsonAction != ToolInputAction.Observe
                || toolInput.SchemaViolationAction != ToolInputAction.Observe);
        return leakBuffers || toolInputBuffers;
    }

    /// <summary>
    /// Warms authentication only for an ordinary launch. An updater-managed
    /// activation deliberately performs zero credential or network access before
    /// readiness; UpdateReadinessReporter resumes it after sending Ready. This
    /// keeps transaction commit independent from external auth and preserves
    /// rollback compatibility with the pre-update credential format.
    /// Returns <see langword="true"/> when authentication was warmed.
    /// </summary>
    internal static async Task<bool> BootstrapAuthenticationAsync(
        IAuthService auth,
        UpdateLaunchContext? updateContext,
        ILogger log,
        CancellationToken cancellationToken)
    {
        if (updateContext is not null)
        {
            log.LogInformation(
                "Updater-managed {UpdateRole} activation: deferring credential migration and network authentication until after Ready",
                updateContext.Role);
            return false;
        }

        if (!auth.IsAuthenticated)
        {
            log.LogInformation(
                "No GitHub token on disk — starting device-code flow. Complete the browser handshake to continue.");
        }

        try
        {
            await auth.EnsureGitHubTokenAsync(cancellationToken).ConfigureAwait(false);
            await auth.GetCopilotTokenAsync(ct: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BridgeStartupException($"Auth setup failed: {ex.Message}", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

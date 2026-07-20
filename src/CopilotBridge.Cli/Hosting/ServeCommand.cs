using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Endpoints.Codex;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Update;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// The <c>serve</c> CLI subcommand. Builds the slim web host, mounts the
/// Claude Code endpoints, and hands off to <c>app.RunAsync</c>. Every concern
/// is delegated to a dedicated extension method so this file stays a 20-line
/// readable shell:
/// <list type="bullet">
///   <item><see cref="BridgeConfigurationExtensions.AddBridgeConfiguration"/> —
///         <c>appsettings.json</c> loading.</item>
///   <item><see cref="BridgeServiceCollectionExtensions.AddBridgeLogging"/> —
///         MEL→Serilog bridge.</item>
///   <item><see cref="BridgeServiceCollectionExtensions.AddBridgeServer"/> —
///         all DI services, options, and hosted services (auth + startup
///         banner happen inside <see cref="BridgeStartupHostedService"/>).</item>
///   <item><see cref="ClaudeCodeMessagesEndpoint.MapMessages"/> etc — endpoint
///         routes self-register from the file holding the handler.</item>
/// </list>
/// </summary>
internal static class ServeCommand
{
    public static async Task<int> RunAsync(int? cliPort, CancellationToken ct)
        => await RunAsync(cliPort, originalArgs: [], ct).ConfigureAwait(false);

    public static async Task<int> RunAsync(int? cliPort, IReadOnlyList<string> originalArgs, CancellationToken ct)
    {
        if (cliPort is { } port && port is < 1 or > 65535)
        {
            throw new BridgeStartupException(
                $"Invalid --port {port}. Must be in 1..65535.");
        }

        // Serve-only startup update gate — runs ONCE, synchronously, before the
        // proxy host is constructed. Every failure is fail-open (logs a Warning
        // and continues). If the updater takes ownership, this process exits
        // cleanly without ever starting Kestrel.
        if (await RunUpdateGateAsync(cliPort, originalArgs, ct).ConfigureAwait(false)
            == UpdateGateDecision.HandedOffToUpdater)
        {
            return 0;
        }

        // Construct the host with config file-watching turned OFF. Left on,
        // CreateSlimBuilder registers appsettings.json / user-secrets with
        // reloadOnChange:true and eagerly starts a recursive FileSystemWatcher over
        // the working directory — which on macOS raises an iCloud/Google-Drive access
        // prompt while the bridge sits idle with no client. The switch must be passed
        // at construction (the host reads it while bootstrapping and the watcher is
        // created inside the builder); the bridge never hot-reloads config, so nothing
        // is lost. See BridgeConfigurationExtensions.DisableConfigFileWatchingArgs.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = BridgeConfigurationExtensions.DisableConfigFileWatchingArgs,
        });

        // Validate the DI graph at build time and enforce scope rules always (not
        // only in Development): a captive dependency — a singleton that injects the
        // now-scoped BridgeContext or any scoped pipeline component — fails the host
        // build instead of silently capturing one request's instance for the process
        // lifetime. This is the guardrail behind the per-request-scoped pipeline.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.AddBridgeConfiguration();
        builder.Logging.AddBridgeLogging();
        builder.Services.AddBridgeServer(builder.Configuration, cliPort, PrintDeviceCode);

        var app = builder.Build();

        app.MapMessages();
        app.MapCountTokens();
        app.MapModels();
        app.MapCodexResponses();

        try
        {
            await app.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean Ctrl+C — host shut itself down.
        }
        return 0;
    }

    /// <summary>
    /// Load configuration far enough to bind <see cref="AutoUpdateOptions"/> and
    /// run the gate. Uses the same web-host-neutral appsettings source the server
    /// uses, so the check honors the exact file the bridge will serve from. A
    /// gate failure never blocks startup — it is fail-open by contract.
    /// </summary>
    private static async Task<UpdateGateDecision> RunUpdateGateAsync(
        int? cliPort, IReadOnlyList<string> originalArgs, CancellationToken ct)
    {
        _ = cliPort;
        var config = new ConfigurationBuilder().AddBridgeAppSettings().Build();
        var options = new AutoUpdateOptions();
        config.GetSection("AutoUpdate").Bind(options);

        var gate = new StartupUpdateGate(
            Microsoft.Extensions.Options.Options.Create(options), originalArgs);
        try
        {
            return await gate.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(
                "Auto-update gate error ({Error}); copilot-bridge will continue with the current version.",
                ex.GetType().Name);
            return UpdateGateDecision.ContinueCurrentVersion;
        }
    }

    /// <summary>
    /// Surface the device-code challenge on the console so the operator can
    /// complete the GitHub OAuth handshake. Stays out of the logger so the
    /// URL/code aren't formatted by the Serilog template (the verification
    /// URI must be a single readable line for the user to paste).
    /// </summary>
    private static void PrintDeviceCode(DeviceCodeChallenge dc)
    {
        Console.WriteLine();
        Console.WriteLine("=== GitHub OAuth required ===");
        Console.WriteLine($"Open:  {dc.VerificationUri}");
        Console.WriteLine($"Code:  {dc.UserCode}");
        Console.WriteLine($"Expires in: {dc.ExpiresIn:hh\\:mm\\:ss}");
        Console.WriteLine();
    }
}

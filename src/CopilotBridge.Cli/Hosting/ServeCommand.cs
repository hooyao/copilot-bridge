using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Endpoints.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    {
        if (cliPort is { } port && port is < 1 or > 65535)
        {
            throw new BridgeStartupException(
                $"Invalid --port {port}. Must be in 1..65535.");
        }

        var builder = WebApplication.CreateSlimBuilder();

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

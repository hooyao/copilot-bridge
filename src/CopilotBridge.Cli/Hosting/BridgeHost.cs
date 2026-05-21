using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Shared DI + middleware wiring for the bridge. Both the production CLI entry
/// (<see cref="KestrelServer"/>) and in-process test fixtures call this to get
/// an identically configured <see cref="WebApplication"/>. Caller owns the
/// <see cref="WebApplicationBuilder"/> (slim builder for AOT in prod, full
/// builder in tests) and supplies <c>Configuration</c> + Kestrel binding.
/// </summary>
internal static class BridgeHost
{
    /// <summary>
    /// Registers every singleton/service the bridge needs. Caller must have
    /// already loaded <see cref="RoutesConfig"/> into <c>builder.Configuration</c>
    /// (or supplied an in-memory equivalent) before invoking.
    /// </summary>
    public static void ConfigureServices(WebApplicationBuilder builder, Action<DeviceCodeChallenge>? deviceCodePrinter = null)
    {
        builder.Services.Configure<RoutesConfig>(builder.Configuration.GetSection("Routing"));

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonContext.Default);
        });

        builder.Services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge/0.1");
            return http;
        });
        builder.Services.AddSingleton(sp => new AuthService(
            sp.GetRequiredService<HttpClient>(),
            deviceCodePrinter));
        builder.Services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        builder.Services.AddSingleton(_ => new CopilotHeaderFactory());
        builder.Services.AddSingleton<ICopilotClient>(sp => new CopilotClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IAuthService>(),
            sp.GetRequiredService<CopilotHeaderFactory>()));

        // Audit sink is owned by the static Serilog logger (see Program.cs).
        // Surface it as a DI singleton too so tests/host can dispose it
        // alongside the rest of the container — Dispose drains the worker
        // queue and is idempotent if Serilog already disposed.
        if (BridgeIoSinkHolder.Instance is { } sink)
        {
            builder.Services.AddSingleton(sink);
        }

        builder.Services.AddSingleton<CopilotModelCatalog>(_ => new CopilotModelCatalog());
        builder.Services.AddSingleton<IModelRegistry>(sp => new CopilotModelRegistry(
            sp.GetRequiredService<CopilotModelCatalog>()));
        builder.Services.AddSingleton(ClaudeCodeInboundAdapter.Instance);
        builder.Services.AddSingleton(ClaudeCodeOutboundAdapter.Instance);
        builder.Services.AddSingleton<IPipelineRunner<MessagesRequest>>(_ => new PipelineRunner<MessagesRequest>());
        builder.Services.AddSingleton(sp => BridgePipelines.BuildAnthropic(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<ICopilotClient>(),
            sp.GetRequiredService<IOptions<RoutesConfig>>()));

        builder.Services.Configure<KestrelServerOptions>(opts =>
        {
            opts.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(15);
            opts.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
        });
    }

    /// <summary>
    /// Runs the post-build startup chores in this order:
    /// validate routes → ensure Copilot auth is valid → load model catalog.
    /// Returns null on success, an error code (1=auth, 2=routes) otherwise.
    /// </summary>
    public static async Task<int?> RunStartupAsync(WebApplication app, CancellationToken ct)
    {
        var routes = app.Services.GetRequiredService<IOptions<RoutesConfig>>().Value;
        try
        {
            RoutesValidator.Validate(routes);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        var auth = app.Services.GetRequiredService<IAuthService>();
        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotBridge.Cli.Startup");
        try
        {
            // First ensure a GitHub OAuth token exists — runs the
            // device-code flow if needed. The injected device-code
            // printer (KestrelServer.PrintDeviceCode) surfaces the
            // verification URI + user code on stdout so the operator
            // can complete the browser handshake. EnsureGitHubTokenAsync
            // blocks (polling GitHub) until they do, then persists the
            // token. After that, exchange it for a Copilot token.
            if (!auth.IsAuthenticated)
            {
                startupLog.LogInformation(
                    "No GitHub token on disk — starting device-code flow. Complete the browser handshake to continue.");
            }
            await auth.EnsureGitHubTokenAsync(ct);
            await auth.GetCopilotTokenAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            startupLog.LogError(ex, "auth setup failed: {Message}", ex.Message);
            return 1;
        }

        var catalog = app.Services.GetRequiredService<CopilotModelCatalog>();
        var copilotClient = app.Services.GetRequiredService<ICopilotClient>();
        try
        {
            await catalog.LoadFromAsync(copilotClient, ct);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"warn: /models load failed: {ex.Message} — effort routing will strip the field for unknown models.");
        }

        return null;
    }

    /// <summary>Mounts the request-handling endpoints. Call after <see cref="RunStartupAsync"/>.</summary>
    public static void MapEndpoints(WebApplication app)
    {
        ClaudeCodeEndpoints.Map(app);
    }
}

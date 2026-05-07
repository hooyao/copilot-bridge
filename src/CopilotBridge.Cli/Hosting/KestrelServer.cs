using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Builds and runs the bridge HTTP server. Uses
/// <see cref="WebApplication.CreateSlimBuilder()"/> — the AOT-friendly variant —
/// and explicit factory registrations everywhere to avoid reflection-based DI.
/// </summary>
internal static class KestrelServer
{
    public static async Task<int> RunAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Hook our source-gen JsonContext into minimal-API binding so any
        // future TypedResults.Json calls use it. Direct serialization in the
        // endpoints already uses JsonContext.Default explicitly.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonContext.Default);
        });

        // Single HttpClient — generous timeout for long thinking turns; the
        // bridge's per-request CancellationToken (from Kestrel) tears down
        // upstream sooner if the inbound client disconnects. The User-Agent is
        // required by api.github.com (without it the request gets a 403).
        builder.Services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge/0.1");
            return http;
        });
        builder.Services.AddSingleton(sp => new AuthService(
            sp.GetRequiredService<HttpClient>(),
            PrintDeviceCode));
        builder.Services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        builder.Services.AddSingleton(_ => new CopilotHeaderFactory());
        builder.Services.AddSingleton<ICopilotClient>(sp => new CopilotClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IAuthService>(),
            sp.GetRequiredService<CopilotHeaderFactory>()));
        builder.Services.AddSingleton(_ => new BridgeRequestLogger());

        // Pipeline framework registrations.
        builder.Services.AddSingleton<IModelRegistry>(_ => new CopilotModelRegistry());
        builder.Services.AddSingleton(ClaudeCodeInboundAdapter.Instance);
        builder.Services.AddSingleton(ClaudeCodeOutboundAdapter.Instance);
        builder.Services.AddSingleton<IPipelineRunner<MessagesRequest>>(_ => new PipelineRunner<MessagesRequest>());
        builder.Services.AddSingleton(sp => BridgePipelines.BuildAnthropic(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<ICopilotClient>()));

        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Services.Configure<KestrelServerOptions>(opts =>
        {
            // Allow long-running streamed responses; default keep-alive would
            // kill thinking-heavy turns mid-stream.
            opts.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(15);
            opts.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
        });

        var app = builder.Build();

        var auth = app.Services.GetRequiredService<IAuthService>();
        try
        {
            await auth.GetCopilotTokenAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"auth check failed: {ex.Message}");
            await Console.Error.WriteLineAsync("Run `copilot-bridge auth login` first.");
            return 1;
        }

        var requestLogger = app.Services.GetRequiredService<BridgeRequestLogger>();
        Console.WriteLine($"copilot-bridge listening on http://localhost:{port}");
        Console.WriteLine($"Upstream: {auth.CopilotApiBaseUrl}");
        Console.WriteLine($"Logs:     {requestLogger.LogDirectory}");
        Console.WriteLine();

        // Pipeline-driven Claude Code endpoints under /cc/v1/...
        ClaudeCodeEndpoints.Map(app);

        await app.RunAsync(ct);
        return 0;
    }

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

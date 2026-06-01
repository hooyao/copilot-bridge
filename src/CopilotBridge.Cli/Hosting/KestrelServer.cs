using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Production CLI entry: builds the AOT-friendly slim host, hands DI/middleware
/// wiring off to <see cref="BridgeHost"/>, then runs Kestrel on the requested
/// port. In-process tests skip this and use <see cref="BridgeHost"/> directly
/// with a full <see cref="WebApplicationBuilder"/>.
/// </summary>
internal static class KestrelServer
{
    public static async Task<int> RunAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Route all Microsoft.Extensions.Logging output (Kestrel, ASP.NET
        // Core hosting/routing diagnostics, etc.) through the same Serilog
        // pipeline that Program.cs configured. Without this, the slim
        // host's default console provider double-logs requests in its own
        // `info: Category[id]\n      Message` format alongside our Serilog
        // template.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SerilogLoggerProvider(dispose: false));

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        BridgeHost.ConfigureServices(builder, PrintDeviceCode);

        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();

        var startupResult = await BridgeHost.RunStartupAsync(app, ct);
        if (startupResult.HasValue) return startupResult.Value;

        var routes = app.Services.GetRequiredService<IOptions<RoutesConfig>>().Value;
        var catalog = app.Services.GetRequiredService<ModelProfileCatalog>();
        var auth = app.Services.GetRequiredService<IAuthService>();
        var traceDir = Logging.BridgeIoSinkHolder.Instance?.Directory;
        var textLogDir = Path.Combine(AppContext.BaseDirectory, "log");

        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotBridge.Cli.Startup");
        startupLog.LogInformation("copilot-bridge listening on http://localhost:{Port}", port);
        startupLog.LogInformation("Upstream: {UpstreamUrl}", auth.CopilotApiBaseUrl);
        startupLog.LogInformation("Text log: {LogDir} (one file per process start)", textLogDir);
        if (traceDir is not null)
        {
            startupLog.LogInformation("Req trace: {TraceDir} (enabled — per-request audit JSON written here)", traceDir);
        }
        else
        {
            startupLog.LogInformation("Req trace: disabled — set Tracing.Enabled=true in appsettings.json to capture per-request bodies");
        }
        startupLog.LogInformation("Routes:    {LocCount} user locations; catalog: {ModelCount} model profiles",
            routes.Locations.Count, catalog.Count);

        BridgeHost.MapEndpoints(app);

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

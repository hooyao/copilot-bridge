using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        BridgeHost.ConfigureServices(builder, PrintDeviceCode);

        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();

        var startupResult = await BridgeHost.RunStartupAsync(app, ct);
        if (startupResult.HasValue) return startupResult.Value;

        var routes = app.Services.GetRequiredService<IOptions<RoutesConfig>>().Value;
        var catalog = app.Services.GetRequiredService<CopilotModelCatalog>();
        var auth = app.Services.GetRequiredService<IAuthService>();
        var requestLogger = app.Services.GetRequiredService<BridgeRequestLogger>();
        Console.WriteLine($"copilot-bridge listening on http://localhost:{port}");
        Console.WriteLine($"Upstream: {auth.CopilotApiBaseUrl}");
        Console.WriteLine($"Logs:     {requestLogger.LogDirectory}");
        Console.WriteLine($"Routes:   {routes.Rules.Count} user rules; catalog: {catalog.Count} Anthropic models");
        Console.WriteLine();

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

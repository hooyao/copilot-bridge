using System.Runtime.Versioning;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Class fixture that boots the bridge in-process on a random localhost port,
/// performs the production startup chain (auth + /models catalog load), and
/// exposes the bound URL + log directory so headless tests can drive
/// <c>claude.exe</c> at it. Disposed once per test class.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BridgeFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private BridgeIoSink? _sink;

    public string BaseUrl { get; private set; } = "";
    public string LogDirectory => _sink?.Directory ?? throw new InvalidOperationException("Bridge not initialized.");

    public async Task InitializeAsync()
    {
        // Mirror production: bootstrap Serilog before anything else, then let
        // the host swap in the full logger via SerilogReplacerHostedService.
        Log.Logger = SerilogBootstrapper.BuildBootstrap();

        var builder = WebApplication.CreateBuilder();

        // Production appsettings.json lives next to copilot-bridge.exe in
        // src/CopilotBridge.Cli/...; the fixture lives in test bin dir. Locate
        // and load it explicitly so the catalog/routing logic exercises the
        // same config the user runs against. Override Tracing.Enabled so the
        // fixture always captures bodies for assertions.
        var prodAppsettings = LocateProductionAppsettings();
        if (prodAppsettings is not null)
        {
            builder.Configuration.AddJsonFile(prodAppsettings, optional: false, reloadOnChange: false);
        }
        var ioDir = Path.Combine(AppContext.BaseDirectory, "request-traces");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:Directory"] = ioDir,
        });

        builder.Logging.AddBridgeLogging();
        builder.Services.AddBridgeServer(builder.Configuration, cliPort: null, deviceCodePrinter: null);

        // Random ephemeral port — but the production AddBridgeServer wires
        // Kestrel via IConfigureOptions<KestrelServerOptions>.ListenLocalhost(port).
        // Override that here with a UseUrls so the OS picks the port.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();

        // Cache the sink so LogDirectory can read it.
        _sink = _app.Services.GetService<BridgeIoSink>();

        _app.MapMessages();
        _app.MapCountTokens();
        _app.MapModels();

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("No IServerAddressesFeature on host.");
        BaseUrl = addresses.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Log.CloseAndFlush();
        _sink?.Dispose();
    }

    /// <summary>
    /// Walks up from the test bin dir to find the production project's
    /// <c>appsettings.json</c>. Lets tests exercise the user's actual routing
    /// rules without copying the file.
    /// </summary>
    private static string? LocateProductionAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CopilotBridge.Cli", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

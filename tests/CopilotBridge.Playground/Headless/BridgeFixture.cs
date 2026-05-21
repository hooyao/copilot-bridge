using System.Runtime.Versioning;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
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
        // The bridge expects a BridgeIoSink registered as both the Serilog
        // sink and a DI singleton. Program.cs handles this for the
        // production binary; tests do it themselves here.
        var ioDir = Path.Combine(AppContext.BaseDirectory, "logs");
        _sink = new BridgeIoSink(ioDir);
        BridgeIoSinkHolder.Instance = _sink;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("Payload"))
                .WriteTo.Sink(_sink))
            .CreateLogger();

        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SerilogLoggerProvider(dispose: false));

        // The fixture lives in test bin dir; production appsettings.json lives
        // next to copilot-bridge.exe in src/CopilotBridge.Cli/bin/.../appsettings.json.
        // Load it explicitly so the catalog/routing logic exercises the same
        // config the user runs against.
        var prodAppsettings = LocateProductionAppsettings();
        if (prodAppsettings is not null)
        {
            builder.Configuration.AddJsonFile(prodAppsettings, optional: false, reloadOnChange: false);
        }

        BridgeHost.ConfigureServices(builder, deviceCodePrinter: null);

        // Random ephemeral port. After app starts, IServerAddressesFeature gives us the actual port.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();

        var startup = await BridgeHost.RunStartupAsync(_app, CancellationToken.None);
        if (startup.HasValue)
        {
            throw new InvalidOperationException($"Bridge startup returned exit code {startup.Value} — auth or routes config issue.");
        }

        BridgeHost.MapEndpoints(_app);

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

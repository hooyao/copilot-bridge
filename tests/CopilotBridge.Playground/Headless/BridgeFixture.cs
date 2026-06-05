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
        // Pick an OS-assigned ephemeral port up front so we don't collide with
        // the user's own bridge on 8765. Can't simply add UseUrls(":0") here:
        // KestrelOptionsConfigurator unconditionally calls
        // options.ListenLocalhost(BridgeServerOptions.Port), which ADDS a
        // listen endpoint (8765 by default) on top of any UseUrls value —
        // and Kestrel then tries to bind BOTH, failing with
        // AddressInUseException whenever the production bridge already owns
        // 8765 (the common dev setup). Overriding Server:Port in config
        // routes through the same code path; production validates >= 1, so
        // we materialize a free port via a transient socket bind.
        var freePort = GetFreeLoopbackPort();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:Directory"] = ioDir,
            ["Server:Port"] = freePort.ToString(),
        });

        builder.Logging.AddBridgeLogging();
        builder.Services.AddBridgeServer(builder.Configuration, cliPort: null, deviceCodePrinter: null);

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

    /// <summary>
    /// Reserve a free loopback TCP port by briefly binding a socket to
    /// <c>127.0.0.1:0</c>, reading the OS-assigned port, and releasing it.
    /// Race window: between this call and Kestrel's own bind another process
    /// could grab the same port — rare on a developer box, and the resulting
    /// AddressInUseException is at least obvious. Avoids the much more common
    /// "production bridge owns 8765" collision that the previous fixture
    /// design ran into.
    /// </summary>
    private static int GetFreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

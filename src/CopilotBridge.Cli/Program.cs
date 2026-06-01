using System.CommandLine;
using System.Reflection;
using System.Runtime.Versioning;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Debug;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

[assembly: SupportedOSPlatform("windows")]

const string ProductName = "copilot-bridge";

// Shared option for the serve command.
var portOption = new Option<int>("--port", "-p")
{
    Description = "Port to listen on",
    DefaultValueFactory = _ => ServeCommand.DefaultPort,
};

var serveCommand = new Command("serve", "Start the HTTP bridge");
serveCommand.Options.Add(portOption);
serveCommand.SetAction((parseResult, _) =>
{
    InitializeLogging();
    return ServeCommand.RunAsync(parseResult.GetValue(portOption));
});

// auth subcommands.
var authLogin = new Command("login", "Log in to GitHub via device-code flow");
authLogin.SetAction((_, _) => { InitializeLogging(); return AuthCommand.LoginAsync(); });

var authWhoami = new Command("whoami", "Verify the saved token by calling api.github.com/user");
authWhoami.SetAction((_, _) => { InitializeLogging(); return AuthCommand.WhoAmIAsync(); });

var authStatus = new Command("status", "Check login status (offline)");
authStatus.SetAction((_, _) => { InitializeLogging(); return Task.FromResult(AuthCommand.Status()); });

var authLogout = new Command("logout", "Delete the encrypted token");
authLogout.SetAction((_, _) => { InitializeLogging(); return Task.FromResult(AuthCommand.Logout()); });

var authCopilotStatus = new Command("copilot-status",
    "Exchange the GitHub token for a Copilot token, print expiry + base URL");
authCopilotStatus.SetAction((_, _) => { InitializeLogging(); return AuthCommand.CopilotStatusAsync(); });

var authCommand = new Command("auth", "GitHub authentication");
authCommand.Subcommands.Add(authLogin);
authCommand.Subcommands.Add(authWhoami);
authCommand.Subcommands.Add(authStatus);
authCommand.Subcommands.Add(authLogout);
authCommand.Subcommands.Add(authCopilotStatus);

// debug subcommands.
var allOption = new Option<bool>("--all", "-a")
{
    Description = "Include all models, not just those advertising /v1/messages",
};
var listModelsCommand = new Command("list-models",
    "List models advertising /v1/messages on Copilot");
listModelsCommand.Options.Add(allOption);
listModelsCommand.SetAction((parseResult, _) =>
{
    InitializeLogging();
    return DebugCommand.ListModelsAsync(parseResult.GetValue(allOption));
});

var debugCommand = new Command("debug", "Diagnostic tools");
debugCommand.Subcommands.Add(listModelsCommand);

// Root.
var rootCommand = new RootCommand(
    $"{ProductName} v{ResolveProductVersion()}: GitHub Copilot reverse proxy for Claude Code");
rootCommand.Subcommands.Add(serveCommand);
rootCommand.Subcommands.Add(authCommand);
rootCommand.Subcommands.Add(debugCommand);
// Default action when no subcommand is given: behave like 'serve' on the default port.
rootCommand.SetAction((_, _) => { InitializeLogging(); return ServeCommand.RunAsync(ServeCommand.DefaultPort); });

// Ensure log buffers flush even on hard exit (Ctrl+C, kill).
AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

return await rootCommand.Parse(args).InvokeAsync();

static void InitializeLogging()
{
    // Two sinks:
    //   1. Console — standard error stream so we don't interleave with stdout
    //      output produced by the auth device-code flow / banners.
    //   2. File — one new file per process start at <exe-dir>/log/bridge-{YYYYMMDD-HHMMSS}.log.
    //      No rolling: each restart gets its own file so a single run is
    //      easy to grep without daily-spanning chunks. Old files accumulate
    //      until the operator cleans them up.
    // Per-request audit JSON (a separate "request trace" stream) is OFF by
    // default — it captures full request/response bodies, which include
    // user prompts. Operators opt in via appsettings.json:
    //     "Tracing": { "Enabled": true, "Directory": "request-traces" }
    // When off, no sink is created and no audit files are written.
    // Level defaults to Verbose at the Serilog layer; per-category filtering
    // is delegated to Microsoft.Extensions.Logging via appsettings.json's
    // "Logging" section, which the slim host wires up automatically.
    var startupStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    var logPath = Path.Combine(AppContext.BaseDirectory, "log", $"bridge-{startupStamp}.log");

    // Read just the Tracing section ourselves — we need the answer before
    // ConfigureServices runs, so the standard Options binding isn't available
    // yet. Two scalar lookups, no POCO binding, AOT-safe.
    var earlyConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .Build();
    var tracingEnabled = string.Equals(
        earlyConfig["Tracing:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    var tracingDir = earlyConfig["Tracing:Directory"];
    if (string.IsNullOrWhiteSpace(tracingDir)) tracingDir = "request-traces";
    var ioLogDir = Path.IsPathRooted(tracingDir)
        ? tracingDir
        : Path.Combine(AppContext.BaseDirectory, tracingDir);
    const string outputTemplate = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    // Custom sink owns per-request inbound/upstream audit JSONs.
    // Created only when tracing is enabled; otherwise the holder stays null
    // and the audit-only Serilog sub-logger is skipped entirely (no files,
    // no channel, no background writer).
    if (tracingEnabled)
    {
        BridgeIoSinkHolder.Instance = new BridgeIoSink(ioLogDir);
    }

    var loggerConfig = new LoggerConfiguration()
        .MinimumLevel.Verbose()
        // Keep BridgeIoPayload as a live reference instead of letting Serilog
        // stringify it: BridgeIoSink reads scalar.Value back as the typed
        // payload. Without this AsScalar registration the MEL→Serilog bridge
        // calls .ToString() on the value (since BridgeIoPayload isn't a known
        // scalar) and the sink silently drops every audit event.
        .Destructure.AsScalar<BridgeIoPayload>();

    if (BridgeIoSinkHolder.Instance is { } auditSink)
    {
        // Bridge IO events go to the audit sink only — keep them out of the
        // rolling text log and stderr to avoid duplication.
        loggerConfig = loggerConfig.WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("Payload"))
            .WriteTo.Sink(auditSink));
    }

    Log.Logger = loggerConfig
        .WriteTo.Logger(lc => lc
            .Filter.ByExcluding(e => e.Properties.ContainsKey("Payload"))
            .WriteTo.Console(outputTemplate: outputTemplate, standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.File(
                path: logPath,
                outputTemplate: outputTemplate,
                flushToDiskInterval: TimeSpan.FromSeconds(2)))
        .CreateLogger();

    Log.Information("copilot-bridge {Version} starting (pid={Pid})", ResolveProductVersion(), Environment.ProcessId);
}

static string ResolveProductVersion()
{
    var info = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
    if (string.IsNullOrEmpty(info)) return "0.0.0-dev";
    var plus = info.IndexOf('+');
    return plus >= 0 ? info[..plus] : info;
}

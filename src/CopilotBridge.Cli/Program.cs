using System.CommandLine;
using System.Reflection;
using System.Runtime.Versioning;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Debug;
using CopilotBridge.Cli.Hosting;
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
    // Level defaults to Debug; operator can dial down with BRIDGE_LOG_LEVEL=Information.
    var startupStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    var logPath = Path.Combine(AppContext.BaseDirectory, "log", $"bridge-{startupStamp}.log");
    const string outputTemplate = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Is(ParseLogLevel(Environment.GetEnvironmentVariable("BRIDGE_LOG_LEVEL")))
        .WriteTo.Console(outputTemplate: outputTemplate, standardErrorFromLevel: LogEventLevel.Verbose)
        .WriteTo.File(
            path: logPath,
            outputTemplate: outputTemplate,
            flushToDiskInterval: TimeSpan.FromSeconds(2))
        .CreateLogger();

    Log.Information("copilot-bridge {Version} starting (pid={Pid})", ResolveProductVersion(), Environment.ProcessId);
}

static LogEventLevel ParseLogLevel(string? s) =>
    Enum.TryParse<LogEventLevel>(s, true, out var v) ? v : LogEventLevel.Debug;

static string ResolveProductVersion()
{
    var info = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
    if (string.IsNullOrEmpty(info)) return "0.0.0-dev";
    var plus = info.IndexOf('+');
    return plus >= 0 ? info[..plus] : info;
}

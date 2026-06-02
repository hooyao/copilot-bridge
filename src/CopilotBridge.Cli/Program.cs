using System.CommandLine;
using System.Reflection;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Logging;
using Serilog;

// Bootstrap a console-only Serilog logger so any early CLI / parser / option
// errors land somewhere readable. SerilogReplacerHostedService later swaps
// this for the full pipeline (console + rolling file + optional audit sink).
Log.Logger = SerilogBootstrapper.BuildBootstrap();
AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

const string ProductName = "copilot-bridge";

// Disable System.CommandLine 2.0's built-in exception handler — without this
// the library swallows our uncaught exceptions and prints "Unhandled
// exception: ..." with a raw stack, bypassing our FatalErrorHandler (which
// renders a friendlier message and pauses for keypress when run from a
// terminal). EnableDefaultExceptionHandler=false lets the exception bubble
// up to the try/catch below instead.
var invocationConfig = new InvocationConfiguration
{
    EnableDefaultExceptionHandler = false,
};

try
{
    return await RootCli.Build(ProductName, ResolveProductVersion())
        .Parse(args)
        .InvokeAsync(invocationConfig);
}
catch (Exception ex)
{
    // Any uncaught exception — bad appsettings.json, port already in use,
    // auth failure, you name it — gets surfaced here. FatalErrorHandler
    // writes the error to stderr and pauses for keypress when attached to a
    // real terminal so a user double-clicking the .exe can read the error
    // before the window vanishes.
    FatalErrorHandler.PauseAndExit(ex);
    return 1;
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

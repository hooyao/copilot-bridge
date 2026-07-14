using System.CommandLine;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Logging;
using Serilog;

// Bootstrap a console-only Serilog logger so any early CLI / parser / option
// errors land somewhere readable. SerilogReplacerHostedService later swaps
// this for the full pipeline (console + rolling file + optional audit sink).
Log.Logger = SerilogBootstrapper.BuildBootstrap();
AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

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
    // Capture the original argument vector verbatim before System.CommandLine
    // parsing, so the updater can relaunch the replacement bridge with the exact
    // same arguments and working directory.
    RootCli.CapturedArgs = args;

    return await RootCli.Build()
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

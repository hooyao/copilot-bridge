using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Two-step Serilog setup. The static <see cref="Log.Logger"/> must exist
/// before <c>WebApplication.CreateSlimBuilder</c> runs (so early System.CommandLine
/// errors have somewhere to go), but the audit sink lives in DI and isn't
/// available until the host is built. So:
/// <list type="number">
///   <item><see cref="BuildBootstrap"/> at the top of <c>Program.cs</c> —
///         console-to-stderr only.</item>
///   <item><see cref="ReplaceWithFull"/> from
///         <see cref="SerilogReplacerHostedService.StartAsync"/> — adds the
///         rolling file and the audit sink (when tracing is enabled),
///         atomically replaces <see cref="Log.Logger"/>.</item>
/// </list>
/// </summary>
internal static class SerilogBootstrapper
{
    private const string OutputTemplate =
        "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Console-only logger written to stderr (so stdout stays clean for any
    /// CLI subcommand that prints structured output). Used until the full
    /// host is built and <see cref="ReplaceWithFull"/> swaps in the
    /// production logger.
    /// </summary>
    /// <remarks>
    /// We deliberately use <c>CreateLogger()</c> rather than the
    /// <c>CreateBootstrapLogger()</c> helper from
    /// <c>Serilog.Extensions.Hosting</c> — the latter would pull in another
    /// NuGet package solely for log-event hand-off across the swap point,
    /// which only matters under concurrent startup logging. Our startup is
    /// single-threaded so a plain swap of the static <see cref="Log.Logger"/>
    /// field is sufficient.
    /// </remarks>
    public static Serilog.ILogger BuildBootstrap()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: OutputTemplate,
                standardErrorFromLevel: LogEventLevel.Verbose)
            .CreateLogger();
    }

    /// <summary>
    /// Build the production logger (console + rolling file + optional audit
    /// sink) and atomically swap it in as <see cref="Log.Logger"/>. The old
    /// (bootstrap) logger is disposed afterward.
    /// </summary>
    public static void ReplaceWithFull(IServiceProvider services)
    {
        var sink = services.GetService<BridgeIoSink>();
        var logPath = BuildLogPath();

        var config = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            // Keep BridgeIoPayload as a live reference instead of letting
            // Serilog stringify it: BridgeIoSink reads scalar.Value back as
            // the typed payload. Without this AsScalar registration the
            // MEL→Serilog bridge calls .ToString() on the value (since
            // BridgeIoPayload isn't a known scalar) and the sink silently
            // drops every audit event.
            .Destructure.AsScalar<BridgeIoPayload>();

        if (sink is not null)
        {
            // Bridge IO events go to the audit sink only — keep them out of
            // the rolling text log and stderr to avoid duplication.
            config = config.WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("Payload"))
                .WriteTo.Sink(sink));
        }

        config = config.WriteTo.Logger(lc => lc
            .Filter.ByExcluding(e => e.Properties.ContainsKey("Payload"))
            .WriteTo.Console(
                outputTemplate: OutputTemplate,
                standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.File(
                path: logPath,
                outputTemplate: OutputTemplate,
                flushToDiskInterval: TimeSpan.FromSeconds(2)));

        var newLogger = config.CreateLogger();
        var oldLogger = Log.Logger;
        Log.Logger = newLogger;
        (oldLogger as IDisposable)?.Dispose();
    }

    /// <summary>Build the rolling text-log path (one file per process start).</summary>
    public static string BuildLogPath()
    {
        var stamp = DateTime.UtcNow.ToString(
            "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(AppContext.BaseDirectory, "log", $"bridge-{stamp}.log");
    }

    /// <summary>
    /// Resolve the tracing directory (absolute or rooted under
    /// <see cref="AppContext.BaseDirectory"/>) from a bound
    /// <see cref="TracingOptions"/>.
    /// </summary>
    public static string ResolveTracingDirectory(IOptions<TracingOptions> tracing)
    {
        var dir = tracing.Value.Directory;
        if (string.IsNullOrWhiteSpace(dir)) dir = "request-traces";
        return Path.IsPathRooted(dir)
            ? dir
            : Path.Combine(AppContext.BaseDirectory, dir);
    }
}

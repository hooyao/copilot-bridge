using System.Reflection;
using System.Runtime.Versioning;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Debug;
using CopilotBridge.Cli.Hosting;

[assembly: SupportedOSPlatform("windows")]

const string ProductName = "copilot-bridge";

// Read at runtime from the assembly's [AssemblyInformationalVersion]
// attribute, which MSBuild auto-emits from -p:Version=X.Y.Z passed by the
// release workflow. No source change needed to bump versions — just push a
// new release-X.Y.Z tag. AOT-safe: the executing assembly's own attributes
// are preserved by default. Strip any "+commit-sha" suffix the deterministic
// build appends.
var productVersion = ResolveProductVersion();

if (args.Length > 0)
{
    switch (args[0])
    {
        case "-v":
        case "--version":
            Console.WriteLine($"{ProductName} {productVersion}");
            return 0;
        case "-h":
        case "--help":
            PrintHelp();
            return 0;
        case "auth":
            return await AuthCommand.RunAsync(args[1..]);
        case "debug":
            return await DebugCommand.RunAsync(args[1..]);
        case "serve":
            return await ServeCommand.RunAsync(args[1..]);
    }
}

// No subcommand → run the server with defaults.
return await ServeCommand.RunAsync(args);

static void PrintHelp()
{
    Console.WriteLine("copilot-bridge: GitHub Copilot reverse proxy for Claude Code.");
    Console.WriteLine();
    Console.WriteLine("Usage: copilot-bridge <command> [args]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  serve                 Start the HTTP bridge (default if no command given)");
    Console.WriteLine("  auth login            Log in to GitHub via device-code flow");
    Console.WriteLine("  auth whoami           Verify the saved token");
    Console.WriteLine("  auth status           Check login status");
    Console.WriteLine("  auth copilot-status   Exchange GitHub token for a Copilot token, print expiry + base URL");
    Console.WriteLine("  auth logout           Delete the encrypted token");
    Console.WriteLine("  debug list-models     List models advertising /v1/messages on Copilot (--all for full list + capabilities)");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -h, --help       Show this help message");
    Console.WriteLine("  -v, --version    Show version");
    Console.WriteLine();
    Console.WriteLine("`serve --help` shows server-specific options (e.g. --port).");
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

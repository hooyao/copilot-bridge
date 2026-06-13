using System.CommandLine;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Debug;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Builds the System.CommandLine 2.x root command tree (serve / auth / debug
/// subcommands). Kept out of <c>Program.cs</c> so the top-level file stays a
/// thin error-handling shell.
/// </summary>
internal static class RootCli
{
    /// <summary>
    /// Construct the command tree. Each subcommand's <c>SetAction</c> returns
    /// a process exit code; cancellation flows through the second
    /// <see cref="CancellationToken"/> argument provided by the System.CommandLine
    /// 2.0 invocation pipeline (which wires up Ctrl+C automatically — no
    /// manual <c>Console.CancelKeyPress</c> needed).
    /// </summary>
    public static RootCommand Build()
    {
        // --- serve ----------------------------------------------------------
        var portOption = new Option<int?>("--port", "-p")
        {
            Description = "Port to listen on (overrides Server:Port in appsettings.json)",
            // Null default → "no override, use the one in appsettings".
            DefaultValueFactory = _ => null,
        };

        var serveCommand = new Command("serve", "Start the HTTP bridge");
        serveCommand.Options.Add(portOption);
        serveCommand.SetAction((parseResult, ct) =>
            ServeCommand.RunAsync(parseResult.GetValue(portOption), ct));

        // --- auth -----------------------------------------------------------
        var authLogin = new Command("login", "Log in to GitHub via device-code flow");
        authLogin.SetAction((_, _) => AuthCommand.LoginAsync());

        var authWhoami = new Command("whoami", "Verify the saved token by calling api.github.com/user");
        authWhoami.SetAction((_, _) => AuthCommand.WhoAmIAsync());

        var authStatus = new Command("status", "Check login status (offline)");
        authStatus.SetAction((_, _) => Task.FromResult(AuthCommand.Status()));

        var authLogout = new Command("logout", "Delete the encrypted token");
        authLogout.SetAction((_, _) => Task.FromResult(AuthCommand.Logout()));

        var authCopilotStatus = new Command("copilot-status",
            "Exchange the GitHub token for a Copilot token, print expiry + base URL");
        authCopilotStatus.SetAction((_, _) => AuthCommand.CopilotStatusAsync());

        var authCommand = new Command("auth", "GitHub authentication");
        authCommand.Subcommands.Add(authLogin);
        authCommand.Subcommands.Add(authWhoami);
        authCommand.Subcommands.Add(authStatus);
        authCommand.Subcommands.Add(authLogout);
        authCommand.Subcommands.Add(authCopilotStatus);

        // --- debug ----------------------------------------------------------
        var allOption = new Option<bool>("--all", "-a")
        {
            Description = "Include all models, not just those advertising /v1/messages",
        };
        var listModelsCommand = new Command("list-models",
            "List models advertising /v1/messages on Copilot");
        listModelsCommand.Options.Add(allOption);
        listModelsCommand.SetAction((parseResult, _) =>
            DebugCommand.ListModelsAsync(parseResult.GetValue(allOption)));

        // Hidden: non-destructive token-store self-test. Used by CI to smoke-test the
        // machine-id-derived encryption on Linux/macOS (which we can't build locally) without
        // requiring a real login. Not shown in --help.
        var selfTestCommand = new Command("selftest-tokenstore",
            "Verify the token store can encrypt/decrypt on this platform (non-destructive)")
        {
            Hidden = true,
        };
        selfTestCommand.SetAction((_, _) => Task.FromResult(DebugCommand.SelfTestTokenStore()));

        var debugCommand = new Command("debug", "Diagnostic tools");
        debugCommand.Subcommands.Add(listModelsCommand);
        debugCommand.Subcommands.Add(selfTestCommand);

        // --- root -----------------------------------------------------------
        var root = new RootCommand(
            $"{ProductInfo.Name} v{ProductInfo.Version}: GitHub Copilot reverse proxy for Claude Code");
        root.Subcommands.Add(serveCommand);
        root.Subcommands.Add(authCommand);
        root.Subcommands.Add(debugCommand);

        // Default action when no subcommand is given: behave like 'serve' on
        // the default port (== whatever appsettings.json declares).
        root.SetAction((_, ct) => ServeCommand.RunAsync(cliPort: null, ct));

        return root;
    }
}

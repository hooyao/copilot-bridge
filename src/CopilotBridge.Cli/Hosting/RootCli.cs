using System.CommandLine;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Debug;
using CopilotBridge.Cli.Hosting.ClientConfig;

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
            ServeCommand.RunAsync(
                parseResult.GetValue(portOption),
                CapturedArgs,
                ct));

        // --- auth -----------------------------------------------------------
        var authLogin = new Command(
            "login",
            "Log in via the built-in GitHub Copilot Plugin device flow (gh is not required)");
        authLogin.SetAction((_, _) => AuthCommand.LoginAsync());

        var authWhoami = new Command("whoami",
            "Validate/refresh the saved GitHub credential via api.github.com/user");
        authWhoami.SetAction((_, _) => AuthCommand.WhoAmIAsync());

        var authStatus = new Command("status",
            "Show encrypted credential format, source, and expiry (offline)");
        authStatus.SetAction((_, _) => Task.FromResult(AuthCommand.Status()));

        var authLogout = new Command("logout", "Delete the encrypted token");
        authLogout.SetAction((_, _) => Task.FromResult(AuthCommand.Logout()));

        var authCopilotStatus = new Command("copilot-status",
            "Resolve direct/exchanged auth and print Copilot mode + base URL (never token bytes)");
        authCopilotStatus.SetAction((_, _) => AuthCommand.CopilotStatusAsync());

        // Hidden command-auth helper for Codex model discovery. It prints a stable,
        // public sentinel and deliberately does not construct AuthService or a host.
        var authProviderToken = new Command("provider-token",
            "Print the non-secret Codex provider sentinel")
        {
            Hidden = true,
        };
        authProviderToken.SetAction((_, _) => Task.FromResult(AuthCommand.ProviderToken()));

        var authCommand = new Command("auth", "GitHub authentication");
        authCommand.Subcommands.Add(authLogin);
        authCommand.Subcommands.Add(authWhoami);
        authCommand.Subcommands.Add(authStatus);
        authCommand.Subcommands.Add(authLogout);
        authCommand.Subcommands.Add(authCopilotStatus);
        authCommand.Subcommands.Add(authProviderToken);

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

        // --- config ---------------------------------------------------------
        // Auto-configure a client (Claude Code / Codex) to point at this bridge.
        // Runs in its own web-host-free composition root (ClientConfigServices) —
        // shares no runtime service with `serve`.
        var configPortOption = new Option<int?>("--port", "-p")
        {
            Description = "Port to write into the client config (overrides Server:Port from appsettings.json)",
            DefaultValueFactory = _ => null,
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the planned configuration without writing any file",
        };
        var showContentOption = new Option<bool>("--show-content")
        {
            Description = "With --dry-run, also print the full merged file (may include preserved secrets)",
        };
        var scopeOption = new Option<ConfigScope>("--scope")
        {
            Description = "Which config to write: global (user-level) or repo (./.claude/settings.local.json)",
            DefaultValueFactory = _ => ConfigScope.Global,
        };

        var configClaudeCode = new Command("claude-code",
            "Point Claude Code at the bridge; update connection/auth only and preserve all behavior settings");
        configClaudeCode.Options.Add(scopeOption);
        configClaudeCode.Options.Add(configPortOption);
        configClaudeCode.Options.Add(dryRunOption);
        configClaudeCode.Options.Add(showContentOption);
        configClaudeCode.SetAction((parseResult, _) => Task.FromResult(
            ConfigCommand.Configure(
                "claude-code",
                parseResult.GetValue(scopeOption),
                parseResult.GetValue(configPortOption),
                parseResult.GetValue(dryRunOption),
                parseResult.GetValue(showContentOption))));

        // This bridge command intentionally edits only the user/global baseline.
        // Codex may apply higher-precedence project/profile/CLI layers later, so no
        // --scope option is offered and the help text states that visibility limit.
        var configCodex = new Command("codex",
            "Point global ~/.codex/config.toml at the bridge; preserve provider behavior fields (project/profile/CLI overrides may supersede it)");
        configCodex.Options.Add(configPortOption);
        configCodex.Options.Add(dryRunOption);
        configCodex.Options.Add(showContentOption);
        configCodex.SetAction((parseResult, _) => Task.FromResult(
            ConfigCommand.Configure(
                "codex",
                ConfigScope.Global,
                parseResult.GetValue(configPortOption),
                parseResult.GetValue(dryRunOption),
                parseResult.GetValue(showContentOption))));

        var configStatus = new Command("status",
            "Show connection/auth drift and observe client behavior values without rewriting them");
        configStatus.Options.Add(configPortOption);
        configStatus.SetAction((parseResult, _) => Task.FromResult(
            ConfigCommand.Status(parseResult.GetValue(configPortOption))));

        var configCommand = new Command("config", "Configure a client connection to use this bridge");
        configCommand.Subcommands.Add(configClaudeCode);
        configCommand.Subcommands.Add(configCodex);
        configCommand.Subcommands.Add(configStatus);

        // --- root -----------------------------------------------------------
        var root = new RootCommand(
            $"{ProductInfo.Name} v{ProductInfo.Version}: GitHub Copilot reverse proxy for Claude Code");
        root.Subcommands.Add(serveCommand);
        root.Subcommands.Add(authCommand);
        root.Subcommands.Add(debugCommand);
        root.Subcommands.Add(configCommand);

        // Default action when no subcommand is given: behave like 'serve' on
        // the default port (== whatever appsettings.json declares).
        root.SetAction((_, ct) => ServeCommand.RunAsync(cliPort: null, CapturedArgs, ct));

        return root;
    }

    /// <summary>
    /// The original process argument vector, captured verbatim in
    /// <see cref="Program"/> before System.CommandLine parsing so the updater can
    /// relaunch the replacement bridge with the exact same arguments (no-argument
    /// startup stays no-argument; <c>serve --port N</c> is preserved element by
    /// element). Empty when not set.
    /// </summary>
    public static IReadOnlyList<string> CapturedArgs { get; set; } = [];
}

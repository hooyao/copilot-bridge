using CopilotBridge.Cli.Hosting.ClientConfig;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Command handlers for the <c>config</c> subcommand family. Thin orchestration:
/// build the isolated composition root, derive the connection facts once, pick the
/// configurator by id, validate scope, then plan and (unless <c>--dry-run</c>) apply.
/// Returns process exit codes like the other command groups (0 ok, non-zero on
/// validation/IO error).
/// </summary>
internal static class ConfigCommand
{
    /// <summary>Configure a client for a given scope.</summary>
    /// <param name="clientId">The configurator id (e.g. <c>"claude-code"</c>,
    /// <c>"codex"</c>).</param>
    /// <param name="scope">Requested scope; validated against the configurator's
    /// supported scopes.</param>
    /// <param name="cliPort">Optional <c>--port</c> override.</param>
    /// <param name="dryRun">When true, print the planned result and write nothing.</param>
    /// <param name="showContent">When true (and <paramref name="dryRun"/>), also print
    /// the full merged file content. Off by default so preserved secrets are not echoed.</param>
    public static int Configure(string clientId, ConfigScope scope, int? cliPort, bool dryRun,
        bool showContent = false)
    {
        if (cliPort is { } p && p is < 1 or > 65535)
        {
            Console.Error.WriteLine($"Invalid --port {p}. Must be in 1..65535.");
            return 1;
        }

        using var provider = ClientConfigServices.Build();
        var configurator = Resolve(provider, clientId);
        if (configurator is null)
        {
            Console.Error.WriteLine($"Unknown client '{clientId}'.");
            return 1;
        }

        if (!configurator.SupportedScopes.Contains(scope))
        {
            var supported = string.Join(", ", configurator.SupportedScopes).ToLowerInvariant();
            Console.Error.WriteLine(
                $"Client '{clientId}' does not support {scope.ToString().ToLowerInvariant()} scope (supported: {supported}).");
            return 1;
        }

        var connection = BridgeConnectionFactory.Create(
            provider.GetRequiredService<IConfiguration>(), cliPort);

        ConfigPlan plan;
        try
        {
            plan = configurator.Plan(connection, scope);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to plan {clientId} config: {ex.Message}");
            return 1;
        }

        PrintPlan(plan, dryRun, showContent);

        if (dryRun)
        {
            return 0;
        }

        if (plan.IsNoOp)
        {
            Console.WriteLine("Already up to date; nothing to write.");
            return 0;
        }

        try
        {
            var backup = configurator.Apply(plan);
            Console.WriteLine($"Wrote {plan.TargetPath}");
            if (backup is not null)
            {
                Console.WriteLine($"Backup: {backup}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write {plan.TargetPath}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Report where each supported client currently points and whether it has
    /// drifted from the current appsettings. Writes no file.</summary>
    public static int Status(int? cliPort)
    {
        using var provider = ClientConfigServices.Build();
        var connection = BridgeConnectionFactory.Create(
            provider.GetRequiredService<IConfiguration>(), cliPort);

        Console.WriteLine($"Bridge endpoint (from appsettings{(cliPort is not null ? " + --port" : "")}): port {connection.Port}");
        Console.WriteLine();

        foreach (var configurator in provider.GetServices<IClientConfigurator>())
        {
            foreach (var scope in configurator.SupportedScopes)
            {
                var label = $"[{configurator.ClientId} / {scope.ToString().ToLowerInvariant()}]";

                ConfigState state;
                try
                {
                    state = configurator.Read(connection, scope);
                }
                catch (Exception ex)
                {
                    // One unreadable client (locked file, permission error, …) must not
                    // sink the whole report — degrade to an error line and keep going.
                    Console.WriteLine($"{label} error");
                    Console.WriteLine($"  could not read: {ex.Message}");
                    Console.WriteLine();
                    continue;
                }

                var status = !state.Exists ? "not configured"
                    : !state.ConfiguredForBridge ? "not pointed at bridge"
                    : state.Drifted ? "DRIFTED"
                    : "configured";

                Console.WriteLine($"{label} {status}");
                Console.WriteLine($"  file: {state.TargetPath}");
                foreach (var detail in state.Details)
                {
                    Console.WriteLine($"  {detail}");
                }
                if (state.Drifted)
                {
                    Console.WriteLine($"  expected base URL: {state.ExpectedBaseUrl}");
                }
                Console.WriteLine();
            }
        }

        return 0;
    }

    private static IClientConfigurator? Resolve(IServiceProvider provider, string clientId) =>
        provider.GetServices<IClientConfigurator>()
            .FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase));

    /// <param name="dryRun">Whether this is a <c>--dry-run</c> (affects wording).</param>
    /// <param name="showFullContent">Whether to print the entire planned file content.
    /// Off by default because the merged file preserves the user's unrelated content,
    /// which can include secrets (an existing <c>ANTHROPIC_AUTH_TOKEN</c>, API keys in
    /// other Codex provider blocks) — echoing it to the console would leak them into
    /// terminal scrollback / shell history / CI logs. The per-key summary already shows
    /// exactly what the command changes.</param>
    private static void PrintPlan(ConfigPlan plan, bool dryRun, bool showFullContent)
    {
        var verb = dryRun ? "Would configure" : "Configuring";
        Console.WriteLine($"{verb} {plan.ClientId} ({plan.Scope.ToString().ToLowerInvariant()} scope)");
        Console.WriteLine($"  file: {plan.TargetPath}{(plan.IsNew ? " (new)" : "")}");
        foreach (var line in plan.Summary)
        {
            Console.WriteLine($"  - {line}");
        }

        if (dryRun && showFullContent)
        {
            Console.WriteLine();
            Console.WriteLine("--- planned file content ---");
            Console.WriteLine(plan.NewContent.TrimEnd('\r', '\n'));
            Console.WriteLine("--- end ---");
        }
        else if (dryRun && !plan.IsNew)
        {
            Console.WriteLine("  (unrelated existing content is preserved and not shown; " +
                "pass --show-content to print the full merged file)");
        }
    }
}

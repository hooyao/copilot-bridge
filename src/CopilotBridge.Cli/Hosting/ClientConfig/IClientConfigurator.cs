namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// The extension seam for auto-configuring one LLM client to point at this bridge.
/// One implementation per supported client (Claude Code, Codex, …). Adding a new
/// client is a new implementation plus one registration line in
/// <see cref="ClientConfigServices.AddClientConfiguration"/> — no change to the
/// command dispatcher or the proxy server.
/// </summary>
/// <remarks>
/// The contract is deliberately split into a pure <see cref="Plan"/> (compute the
/// intended file content, no I/O) and an effectful <see cref="Apply"/> (backup +
/// write). This makes <c>--dry-run</c> free (print the plan, never apply) and
/// idempotence testable (same inputs → same plan → same bytes). <see cref="Read"/>
/// backs <c>config status</c> and writes nothing.
/// </remarks>
internal interface IClientConfigurator
{
    /// <summary>Stable id used on the command line and in status output
    /// (e.g. <c>"claude-code"</c>, <c>"codex"</c>).</summary>
    string ClientId { get; }

    /// <summary>Scopes this client supports. Claude Code: Global + Repo.
    /// Codex: Global only. The dispatcher validates a requested scope against this
    /// before calling <see cref="Plan"/>.</summary>
    IReadOnlyList<ConfigScope> SupportedScopes { get; }

    /// <summary>
    /// Compute the surgical merge result for <paramref name="scope"/> without
    /// touching the filesystem beyond reading the current file. Pure with respect to
    /// output: identical inputs yield an identical <see cref="ConfigPlan.NewContent"/>.
    /// </summary>
    ConfigPlan Plan(BridgeConnection connection, ConfigScope scope);

    /// <summary>
    /// Persist a plan: back up the prior file (if any) then write
    /// <see cref="ConfigPlan.NewContent"/>. A no-op plan writes nothing.
    /// </summary>
    void Apply(ConfigPlan plan);

    /// <summary>Read the client's current bridge-configuration state for
    /// <c>config status</c>. Writes nothing.</summary>
    ConfigState Read(BridgeConnection connection, ConfigScope scope);
}

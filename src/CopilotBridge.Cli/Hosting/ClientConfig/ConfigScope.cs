namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Which config file a client configurator targets. Claude Code supports both;
/// Codex supports only <see cref="Global"/> (its CLI honors a single global
/// <c>config.toml</c>).
/// </summary>
internal enum ConfigScope
{
    /// <summary>The user-level config (e.g. <c>~/.claude/settings.json</c>,
    /// <c>$CODEX_HOME/config.toml</c>).</summary>
    Global = 0,

    /// <summary>The per-repository, personal (gitignored) config
    /// (<c>./.claude/settings.local.json</c>). Claude Code only.</summary>
    Repo = 1,
}

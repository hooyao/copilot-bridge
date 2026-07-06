namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// The connection facts a client configurator writes into a client's config,
/// derived once from <c>appsettings.json</c> (plus an optional <c>--port</c>
/// override) so every configurator and <c>config status</c> share one source of
/// truth. Purely data — no I/O.
/// </summary>
/// <param name="Port">The TCP port the bridge listens on: the CLI <c>--port</c>
/// override if given, else <c>Server:Port</c> from appsettings (default 8765).</param>
/// <param name="NeedNonStreamingFallbackDisabled">True when any response detector can
/// abort mid-stream: the ResponseLeakGuard or ToolInputValidation detector with
/// <c>Enabled &amp;&amp; PreserveStream</c>, or the RunawayGuard detector with
/// <c>Enabled</c> (it has no <c>PreserveStream</c> toggle and always aborts
/// mid-stream). Claude Code only: when true the configurator writes
/// <c>CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1</c> so a mid-stream detector abort
/// forces a whole-turn retry; when false it removes that env key.</param>
internal sealed record BridgeConnection(int Port, bool NeedNonStreamingFallbackDisabled)
{
    /// <summary>Base URL Claude Code points <c>ANTHROPIC_BASE_URL</c> at.</summary>
    public string ClaudeCodeBaseUrl => $"http://localhost:{Port}/cc";

    /// <summary>Base URL the Codex provider block points <c>base_url</c> at.</summary>
    public string CodexBaseUrl => $"http://localhost:{Port}/codex";
}

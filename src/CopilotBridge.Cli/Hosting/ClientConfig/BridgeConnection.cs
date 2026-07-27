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
/// mid-stream). Retained as a detector-state fact for status/reporting; Claude Code
/// configuration no longer disables non-streaming fallback because real 2.1.207
/// uses that request as its recovery path.</param>
/// <param name="ClaudeCodeStreamIdleTimeoutMs">Value to force-write for Claude
/// Code's <c>CLAUDE_STREAM_IDLE_TIMEOUT_MS</c>, derived from the bridge's
/// stream-idle budget so the client's idle watchdogs outlast it. See
/// <see cref="ClaudeCodeTimeoutPolicy"/>.</param>
/// <param name="ClaudeCodeRequestTimeoutMs">Value to force-write for Claude
/// Code's <c>API_TIMEOUT_MS</c>: a <b>fixed</b> residual whole-request ceiling,
/// NOT derived from any budget. It is a wall-clock cap while the bridge's budgets
/// bound inactivity, so no finite value can be guaranteed to outlast them; it is
/// still raised because it also caps each attempt of the client's non-streaming
/// recovery request. See <see cref="ClaudeCodeTimeoutPolicy.RequestTimeoutMs"/>.</param>
internal sealed record BridgeConnection(
    int Port,
    bool NeedNonStreamingFallbackDisabled,
    int ClaudeCodeStreamIdleTimeoutMs,
    int ClaudeCodeRequestTimeoutMs)
{
    /// <summary>Base URL Claude Code points <c>ANTHROPIC_BASE_URL</c> at.</summary>
    public string ClaudeCodeBaseUrl => $"http://localhost:{Port}/cc";

    /// <summary>Base URL the Codex provider block points <c>base_url</c> at.</summary>
    public string CodexBaseUrl => $"http://localhost:{Port}/codex";
}

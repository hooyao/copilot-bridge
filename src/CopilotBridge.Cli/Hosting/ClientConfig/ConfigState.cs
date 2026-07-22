namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// A read-only snapshot of a client's current bridge configuration, produced by a
/// configurator's <c>Read</c> step for <c>config status</c>. Reports where the
/// client currently points and whether that has drifted from what the current
/// <c>appsettings.json</c> would produce. Performs no writes.
/// </summary>
/// <param name="ClientId">The client this state describes.</param>
/// <param name="Scope">The scope that was read.</param>
/// <param name="TargetPath">The file that was inspected.</param>
/// <param name="Exists">Whether that file exists.</param>
/// <param name="ConfiguredForBridge">True when the file currently points at a
/// bridge endpoint (i.e. the configurator's managed keys are present).</param>
/// <param name="CurrentBaseUrl">The base URL the client is currently pointed at, or
/// <c>null</c> if not configured for the bridge.</param>
/// <param name="ExpectedBaseUrl">The base URL the current appsettings-derived
/// connection would write.</param>
/// <param name="ExpectedFallback">The fallback-env value the current bridge
/// configuration would write, or <c>null</c> when it writes none. Claude Code now
/// expects <c>null</c> because the bridge keeps non-streaming recovery enabled;
/// clients without a fallback-env concept (e.g. Codex) pass <c>null</c> for both
/// this and <paramref name="CurrentFallback"/>.</param>
/// <param name="CurrentFallback">The fallback-env value currently stored in the
/// client's file, or <c>null</c> if unset.</param>
/// <param name="ExpectedAssume1m">The value the bridge would force-write for
/// <c>_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL</c> (<c>"1"</c> for Claude Code, so
/// its native-1M capability gate fires for a custom base URL), or <c>null</c> for a
/// client that does not manage it (Codex).</param>
/// <param name="CurrentAssume1m">The value currently stored for that key in the
/// client's file, or <c>null</c> if unset / not pointed at the bridge.</param>
/// <param name="ExpectedDisableErrorReporting">The value the bridge would
/// force-write for <c>DISABLE_ERROR_REPORTING</c> (<c>"1"</c> for Claude Code, to
/// neutralize the telemetry the first-party assertion would otherwise enable), or
/// <c>null</c> for a client that does not manage it (Codex).</param>
/// <param name="CurrentDisableErrorReporting">The value currently stored for that
/// key, or <c>null</c> if unset / not pointed at the bridge.</param>
/// <param name="Details">Extra human-readable lines (e.g. the fallback-env state)
/// shown under <c>config status</c>.</param>
internal sealed record ConfigState(
    string ClientId,
    ConfigScope Scope,
    string TargetPath,
    bool Exists,
    bool ConfiguredForBridge,
    string? CurrentBaseUrl,
    string ExpectedBaseUrl,
    string? ExpectedFallback,
    string? CurrentFallback,
    string? ExpectedAssume1m,
    string? CurrentAssume1m,
    string? ExpectedDisableErrorReporting,
    string? CurrentDisableErrorReporting,
    IReadOnlyList<string> Details)
{
    /// <summary>
    /// True when the client is configured for the bridge but its stored config no
    /// longer matches what the current bridge configuration would produce — either
    /// the base URL (e.g. the port changed), the fallback-env value (e.g. a legacy
    /// <c>CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK</c> key remains in Claude Code's
    /// settings even though the bridge now removes it), or a missing / non-managed
    /// value for either 1M-context env key the bridge force-writes.
    /// </summary>
    public bool Drifted => ConfiguredForBridge &&
        (!string.Equals(CurrentBaseUrl, ExpectedBaseUrl, System.StringComparison.Ordinal) ||
         !string.Equals(CurrentFallback, ExpectedFallback, System.StringComparison.Ordinal) ||
         !string.Equals(CurrentAssume1m, ExpectedAssume1m, System.StringComparison.Ordinal) ||
         !string.Equals(CurrentDisableErrorReporting, ExpectedDisableErrorReporting, System.StringComparison.Ordinal));
}

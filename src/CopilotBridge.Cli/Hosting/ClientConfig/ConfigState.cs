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
/// <param name="ExpectedFallback">The fallback-env value the current appsettings
/// would write (<c>"1"</c> when a detector preserves the stream), or <c>null</c> when
/// it would write none. Clients without a fallback-env concept (e.g. Codex) pass
/// <c>null</c> for both this and <paramref name="CurrentFallback"/>, so it never
/// contributes to <see cref="Drifted"/>.</param>
/// <param name="CurrentFallback">The fallback-env value currently stored in the
/// client's file, or <c>null</c> if unset.</param>
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
    IReadOnlyList<string> Details)
{
    /// <summary>
    /// True when the client is configured for the bridge but its stored config no
    /// longer matches what the current appsettings would produce — either the base URL
    /// (e.g. the port changed) or the fallback-env value (e.g. a detector's
    /// <c>PreserveStream</c>/<c>Enabled</c> changed, so the expected
    /// <c>CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK</c> differs from what is stored).
    /// </summary>
    public bool Drifted => ConfiguredForBridge &&
        (!string.Equals(CurrentBaseUrl, ExpectedBaseUrl, System.StringComparison.Ordinal) ||
         !string.Equals(CurrentFallback, ExpectedFallback, System.StringComparison.Ordinal));
}

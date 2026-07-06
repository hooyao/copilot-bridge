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
    IReadOnlyList<string> Details)
{
    /// <summary>
    /// True when the client is configured for the bridge but its stored base URL no
    /// longer matches what the current appsettings would produce (e.g. the port was
    /// changed in appsettings after the client was configured).
    /// </summary>
    public bool Drifted => ConfiguredForBridge &&
        !string.Equals(CurrentBaseUrl, ExpectedBaseUrl, System.StringComparison.Ordinal);
}
